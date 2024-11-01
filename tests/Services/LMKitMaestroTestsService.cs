﻿using LMKitMaestro.Data;
using LMKitMaestro.Helpers;
using LMKitMaestro.Services;
using LMKitMaestro.Tests.Services;
using LMKitMaestro.ViewModels;
using Microsoft.Extensions.Logging;
using Mopups.Interfaces;
using Moq;

namespace LMKitMaestro.Tests
{
    internal class LMKitMaestroTestsService
    {
        public static readonly Uri Model1 = new(@"https://huggingface.co/lm-kit/phi-3.1-mini-4k-3.8b-instruct-gguf/resolve/main/Phi-3.1-mini-4k-Instruct-Q3_K_M.gguf?download=true");
        public static readonly Uri Model2 = new(@"https://huggingface.co/lm-kit/llama-3-8b-instruct-gguf/resolve/main/Llama-3-8B-Instruct-Q4_K_M.gguf?download=true");

        private Exception? _errorLoadingException;
        TaskCompletionSource<bool>? _modelLoadingTask;
        TaskCompletionSource<bool>? _modelUnloadedTask;

        public bool ProgressEventWasRaided { get; private set; }

        public ILLMFileManager LLmFileManager { get; } = new DummyLLmFileManager();
        public LMKitService LmKitService { get; } = new LMKitService();
        public ILMKitMaestroDatabase Database { get; } = new DummyLMKitMaestroDatabase();

        public SettingsViewModel SettingsViewModel { get; }
        public ConversationListViewModel ConversationListViewModel { get; }
        public ModelListViewModel ModelListViewModel { get; }
        public ChatPageViewModel ChatPageViewModel { get; }

        public LMKitMaestroTestsService()
        {
            SettingsViewModel = GetNewSettingsViewModel(LmKitService);
            ConversationListViewModel = GetNewConversationListViewModel(LmKitService, Database);
            ModelListViewModel = GetNewModelListViewModel(LmKitService, LLmFileManager);
            ChatPageViewModel = GetNewChatPageViewModel(LmKitService, ConversationListViewModel, ModelListViewModel, Database, LLmFileManager, SettingsViewModel);
            LmKitService.LMKitConfig.MaximumCompletionTokens = 200;
            LmKitService.LMKitConfig.RequestTimeout = 15;
        }

        public LMKitService.Conversation GetNewLmKitConversation()
        {
            return new LMKitService.Conversation(LmKitService);
        }

        public ConversationViewModelWrapper GetNewConversationViewModel()
        {
            ConversationListViewModel.AddNewConversation();

            return new ConversationViewModelWrapper(ConversationListViewModel.CurrentConversation!);
        }

        public async Task<bool> LoadModel(Uri? modelUri = null)
        {
            if (modelUri == null)
            {
                modelUri = Model1;
            }

            _modelLoadingTask = new TaskCompletionSource<bool>();
            ProgressEventWasRaided = false;
            LmKitService.ModelLoadingProgressed += LmKitService_ModelLoadingProgressed;
            LmKitService.ModelLoadingCompleted += LmKitService_ModelLoadingCompleted;
            LmKitService.ModelLoadingFailed += LmKitService_ModelLoadingFailed;

            string? localFilePath = FileHelpers.GetModelFilePathFromUrl(modelUri, LMKitDefaultSettings.DefaultModelsFolderPath);

            if (localFilePath == null)
            {
                throw new Exception("Failed to create local file path from model download url");
            }

            LmKitService.LoadModel(modelUri!, localFilePath);


            var result = await _modelLoadingTask.Task;

            return result;
        }

        public async Task<bool> UnloadModel()
        {
            _modelUnloadedTask = new TaskCompletionSource<bool>();
            LmKitService.ModelUnloaded += OnModelUnloaded;
            LmKitService.UnloadModel();


            var result = await _modelUnloadedTask.Task;

            return result;
        }

        private void OnModelUnloaded(object? sender, EventArgs e)
        {
            _modelUnloadedTask?.SetResult(true);
        }

        private void LmKitService_ModelLoadingProgressed(object? sender, EventArgs e)
        {
            var args = (LMKitService.ModelLoadingProgressedEventArgs)e;
            ProgressEventWasRaided = true;
        }

        private void LmKitService_ModelLoadingCompleted(object? sender, EventArgs e)
        {
            _modelLoadingTask?.TrySetResult(true);
        }

        private void LmKitService_ModelLoadingFailed(object? sender, EventArgs e)
        {
            var args = (LMKitService.ModelLoadingFailedEventArgs)e;

            _errorLoadingException = args.Exception;
            _modelLoadingTask?.TrySetResult(false);
        }

        private static ConversationViewModel GetNewConversationViewModel(LMKitService lmKitService, ILMKitMaestroDatabase database)
        {
            var popupService = new Mock<IPopupService>().Object;
            var mainThread = new Mock<IMainThread>().Object;
            var appSettingsService = new Mock<IAppSettingsService>().Object;

            return new ConversationViewModel(mainThread, popupService, appSettingsService, lmKitService, database);
        }

        private static SettingsViewModel GetNewSettingsViewModel(LMKitService lmKitService)
        {
            var appSettingsService = new Mock<IAppSettingsService>().Object;

            return new SettingsViewModel(appSettingsService, lmKitService);
        }

        private static ConversationListViewModel GetNewConversationListViewModel(LMKitService lmKitService, ILMKitMaestroDatabase database)
        {
            var popupService = new Mock<IPopupService>().Object;
            var mainThread = new Mock<IMainThread>().Object;
            var appSettingsService = new Mock<IAppSettingsService>().Object;
            var logger = new Mock<ILogger<ConversationListViewModel>>().Object;

            return new ConversationListViewModel(mainThread, popupService, logger, database, lmKitService, appSettingsService);
        }

        private static ModelListViewModel GetNewModelListViewModel(LMKitService lmKitService, ILLMFileManager llmFileManager)
        {
            var mainThread = new Mock<IMainThread>().Object;

            return new ModelListViewModel(mainThread, llmFileManager, lmKitService);
        }

        private static ChatPageViewModel GetNewChatPageViewModel(LMKitService lmKitService, ConversationListViewModel conversationListViewModel,
            ModelListViewModel modelListViewModel, ILMKitMaestroDatabase database,  ILLMFileManager llmFileManager, SettingsViewModel settingsViewModel)
        {
            var popupService = new Mock<IPopupService>().Object;
            var mainThread = new Mock<IMainThread>().Object;
            var appSettingsService = new Mock<IAppSettingsService>().Object;
            var logger = new Mock<ILogger<ChatPageViewModel>>().Object;
            var navigationService = new Mock<INavigationService>().Object;
            var popupNavigation = new Mock<IPopupNavigation>().Object;

            return new ChatPageViewModel(navigationService, popupService, popupNavigation, conversationListViewModel, 
                modelListViewModel, logger, database, lmKitService, llmFileManager, settingsViewModel);
        }
    }
}
