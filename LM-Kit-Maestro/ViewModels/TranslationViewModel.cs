﻿using CommunityToolkit.Mvvm.ComponentModel;
using LMKit.Maestro.Services;
using LMKit.TextGeneration;

namespace LMKit.Maestro.ViewModels
{
    public partial class TranslationViewModel : AssistantViewModelBase
    {
        [ObservableProperty]
        Language _outputLanguage = Language.English;

        [ObservableProperty]
        Language _inputLanguage = Language.Undefined;

        [ObservableProperty]
        string? _latestResult;

        [ObservableProperty]
        bool? _lastTranslationIsSuccesful;

        public EventHandler? TranslationCompleted;
        public EventHandler? TranslationFailed;

        public TranslationViewModel(IPopupService popupService, LMKitService lmKitService) : base(popupService, lmKitService)
        {
        }

        protected override void HandleSubmit()
        {
            string input = InputText;

            Task.Run(async () =>
            {
                try
                {
                    var result = await _lmKitService.SubmitTranslation(input, OutputLanguage);
                    OnTranslationResult(result);
                }
                catch (Exception ex)
                {
                    OnTranslationResult(null, ex);
                }

                AwaitingResponse = false;
            });
        }

        private void OnTranslationResult(string? result, Exception? exception = null)
        {
            LatestResult = result;
            LastTranslationIsSuccesful = exception == null && result != null;

            if (exception != null)
            {
                TranslationFailed?.Invoke(this, new TranslationCompletedEventArgs(null, exception));
            }
            else
            {
                TranslationCompleted?.Invoke(this, new TranslationCompletedEventArgs(result, null));
            }
        }

        protected override async Task HandleCancel(bool shouldAwait)
        {
            //todo
            await Task.FromException(new NotImplementedException());
        }

        public sealed class TranslationCompletedEventArgs : EventArgs
        {
            public Exception? Exception { get; }

            public string? Translation { get; }

            public TranslationCompletedEventArgs(string? translation, Exception? exception = null)
            {
                Translation = translation;
                Exception = exception;
            }
        }
    }
}
