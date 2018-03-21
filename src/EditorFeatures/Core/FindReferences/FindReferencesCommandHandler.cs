﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.CodeAnalysis.Editor.Commands;
using Microsoft.CodeAnalysis.Editor.FindUsages;
using Microsoft.CodeAnalysis.Editor.Host;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Options;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.FindUsages;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Editor.FindReferences
{
    [ExportCommandHandler(PredefinedCommandHandlerNames.FindReferences, ContentTypeNames.RoslynContentType)]
    internal class FindReferencesCommandHandler : ICommandHandler<FindReferencesCommandArgs>
    {
        private readonly IEnumerable<Lazy<IStreamingFindUsagesPresenter>> _streamingPresenters;

        private readonly IWaitIndicator _waitIndicator;
        private readonly IAsynchronousOperationListener _asyncListener;

        [ImportingConstructor]
        internal FindReferencesCommandHandler(
            IWaitIndicator waitIndicator,
            [ImportMany] IEnumerable<Lazy<IStreamingFindUsagesPresenter>> streamingPresenters,
            IAsynchronousOperationListenerProvider listenerProvider)
        {
            Contract.ThrowIfNull(streamingPresenters);
            Contract.ThrowIfNull(listenerProvider);

            _waitIndicator = waitIndicator;
            _streamingPresenters = streamingPresenters;
            _asyncListener = listenerProvider.GetListener(FeatureAttribute.FindReferences);
        }

        public CommandState GetCommandState(FindReferencesCommandArgs args, Func<CommandState> nextHandler)
        {
            return nextHandler();
        }

        public void ExecuteCommand(FindReferencesCommandArgs args, Action nextHandler)
        {
            // Get the selection that user has in our buffer (this also works if there
            // is no selection and the caret is just at a single position).  If we 
            // can't get the selection, or there are multiple spans for it (i.e. a 
            // box selection), then don't do anything.
            var snapshotSpans = args.TextView.Selection.GetSnapshotSpansOnBuffer(args.SubjectBuffer);
            if (snapshotSpans.Count == 1)
            {
                var selectedSpan = snapshotSpans[0];
                var snapshot = args.SubjectBuffer.CurrentSnapshot;
                var document = snapshot.GetOpenDocumentInCurrentContextWithChanges();
                if (document != null)
                {
                    // Do a find-refs at the *start* of the selection.  That way if the
                    // user has selected a symbol that has another symbol touching it
                    // on the right (i.e.  Goo++  ), then we'll do the find-refs on the
                    // symbol selected, not the symbol following.
                    if (TryExecuteCommand(selectedSpan.Start, document))
                    {
                        return;
                    }
                }
            }

            nextHandler();
        }

        private bool TryExecuteCommand(int caretPosition, Document document)
        {
            var streamingService = document.GetLanguageService<IFindUsagesService>();
            var streamingPresenter = GetStreamingPresenter();

            // See if we're running on a host that can provide streaming results.
            // We'll both need a FAR service that can stream results to us, and 
            // a presenter that can accept streamed results.
            if (streamingService != null && streamingPresenter != null)
            {
                StreamingFindReferences(document, caretPosition, streamingService, streamingPresenter);
                return true;
            }

            return false;
        }

        private IStreamingFindUsagesPresenter GetStreamingPresenter()
        {
            try
            {
                return _streamingPresenters.FirstOrDefault()?.Value;
            }
            catch
            {
                return null;
            }
        }

        private async void StreamingFindReferences(
            Document document, int caretPosition,
            IFindUsagesService findUsagesService,
            IStreamingFindUsagesPresenter presenter)
        {
            try
            {
                using (var token = _asyncListener.BeginAsyncOperation(nameof(StreamingFindReferences)))
                {
                    // Let the presented know we're starting a search.  It will give us back
                    // the context object that the FAR service will push results into.
                    var context = presenter.StartSearch(
                        EditorFeaturesResources.Find_References, supportsReferences: true);

                    using (Logger.LogBlock(
                        FunctionId.CommandHandler_FindAllReference,
                        KeyValueLogMessage.Create(LogType.UserAction, m => m["type"] = "streaming"),
                        context.CancellationToken))
                    {
                        await findUsagesService.FindReferencesAsync(document, caretPosition, context).ConfigureAwait(false);

                        // Note: we don't need to put this in a finally.  The only time we might not hit
                        // this is if cancellation or another error gets thrown.  In the former case,
                        // that means that a new search has started.  We don't care about telling the
                        // context it has completed.  In the latter case something wrong has happened
                        // and we don't want to run any more code in this particular context.
                        await context.OnCompletedAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
            }
        }
    }
}
