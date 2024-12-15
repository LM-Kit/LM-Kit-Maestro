﻿using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LMKit.Maestro.Services;
using System.ComponentModel;

namespace LMKit.Maestro.ViewModels
{
    public abstract partial class AssistantSessionViewModelBase : ViewModelBase
    {
        [ObservableProperty]
        bool _inputTextIsEmpty;

        [ObservableProperty]
        string _inputText = string.Empty;

        [ObservableProperty]
        bool _awaitingResponse;

        protected IPopupService _popupService;

        protected LMKitService _lmKitService;

        [RelayCommand]
        public void Submit()
        {
            if (_lmKitService.ModelLoadingState != LMKitModelLoadingState.Loaded)
            {
                _popupService.DisplayAlert("No model is loaded", "You need to load a model in order to submit a prompt", "OK");
            }
            else
            {
                AwaitingResponse = true;
                HandleSubmit();
                InputText = string.Empty;
            }
        }

        [RelayCommand]
        public async Task Cancel()
        {
            if (AwaitingResponse)
            {
                await HandleCancel(false);
            }
        }

        protected abstract void HandleSubmit();

        protected abstract Task HandleCancel(bool shouldAwait);

        protected AssistantSessionViewModelBase(IPopupService popupService, LMKitService lmKitService)
        {
            _popupService = popupService;
            _lmKitService = lmKitService;
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName == nameof(InputText))
            {
                InputTextIsEmpty = string.IsNullOrWhiteSpace(InputText);
            }
        }
    }
}
