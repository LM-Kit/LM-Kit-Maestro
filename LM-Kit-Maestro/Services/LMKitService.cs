﻿using System.ComponentModel;
using LMKit.TextGeneration;
using LMKit.TextGeneration.Sampling;
using LMKit.TextGeneration.Chat;
using LMKit.Translation;

namespace LMKit.Maestro.Services;

public partial class LMKitService : INotifyPropertyChanged
{
    private readonly SemaphoreSlim _lmKitServiceSemaphore = new SemaphoreSlim(1);

    private readonly PromptSchedule _titleGenerationSchedule = new PromptSchedule();
    private readonly PromptSchedule _promptSchedule = new PromptSchedule();
    private readonly PromptSchedule _translationsSchedule = new PromptSchedule();

    private Uri? _currentlyLoadingModelUri;
    private SingleTurnConversation? _singleTurnConversation;
    private Conversation? _lastConversationUsed = null;
    private LMKit.Model.LLM? _model;
    private MultiTurnConversation? _multiTurnConversation;

    private TextTranslation? _textTranslation;

    public LMKitConfig LMKitConfig { get; } = new LMKitConfig();

    public event NotifyModelStateChangedEventHandler? ModelLoadingProgressed;
    public event NotifyModelStateChangedEventHandler? ModelLoadingCompleted;
    public event NotifyModelStateChangedEventHandler? ModelLoadingFailed;
    public event NotifyModelStateChangedEventHandler? ModelUnloaded;
    public event PropertyChangedEventHandler? PropertyChanged;

    public delegate void NotifyModelStateChangedEventHandler(object? sender, NotifyModelStateChangedEventArgs notifyModelStateChangedEventArgs);

    private LMKitModelLoadingState _modelLoadingState;
    public LMKitModelLoadingState ModelLoadingState
    {
        get => _modelLoadingState;
        set
        {
            _modelLoadingState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModelLoadingState)));
        }
    }

    public void LoadModel(Uri fileUri, string? localFilePath = null)
    {
        if (_model != null)
        {
            UnloadModel();
        }

        _lmKitServiceSemaphore.Wait();
        _currentlyLoadingModelUri = fileUri;
        ModelLoadingState = LMKitModelLoadingState.Loading;

        var modelLoadingTask = new Task(() =>
        {
            bool modelLoadingSuccess;

            try
            {
                _model = new LMKit.Model.LLM(fileUri, fileUri.IsFile ? fileUri.LocalPath : localFilePath, loadingProgress: OnModelLoadingProgressed);

                modelLoadingSuccess = true;
            }
            catch (Exception exception)
            {
                modelLoadingSuccess = false;
                ModelLoadingFailed?.Invoke(this, new ModelLoadingFailedEventArgs(fileUri, exception));
            }
            finally
            {
                _currentlyLoadingModelUri = null;
                _lmKitServiceSemaphore.Release();
            }

            if (modelLoadingSuccess)
            {
                LMKitConfig.LoadedModelUri = fileUri!;

                ModelLoadingCompleted?.Invoke(this, new NotifyModelStateChangedEventArgs(LMKitConfig.LoadedModelUri));
                ModelLoadingState = LMKitModelLoadingState.Loaded;
            }
            else
            {
                ModelLoadingState = LMKitModelLoadingState.Unloaded;
            }

        });

        modelLoadingTask.Start();
    }

    public void UnloadModel()
    {
        // Ensuring we don't clean things up while a model is already being loaded,
        // or while the currently loaded model instance should not be touched
        // (while we are getting Lm-Kit objects ready to process a newly submitted prompt for instance).
        _lmKitServiceSemaphore.Wait();

        var unloadedModelUri = LMKitConfig.LoadedModelUri!;

        if (_promptSchedule.RunningPromptRequest != null && !_promptSchedule.RunningPromptRequest.CancellationTokenSource.IsCancellationRequested)
        {
            _promptSchedule.RunningPromptRequest.CancelAndAwaitTermination();
        }
        else if (_promptSchedule.Count > 1)
        {
            // A prompt is scheduled, but it is not running. 
            _promptSchedule.Next!.CancelAndAwaitTermination();
        }

        if (_titleGenerationSchedule.RunningPromptRequest != null && !_titleGenerationSchedule.RunningPromptRequest.CancellationTokenSource.IsCancellationRequested)
        {
            _titleGenerationSchedule.RunningPromptRequest.CancelAndAwaitTermination();
        }
        else if (_promptSchedule.Count > 1)
        {
            _titleGenerationSchedule.Next!.CancelAndAwaitTermination();
        }

        if (_multiTurnConversation != null)
        {
            _multiTurnConversation.Dispose();
            _multiTurnConversation = null;
        }

        if (_model != null)
        {
            _model.Dispose();
            _model = null;
        }

        _lmKitServiceSemaphore.Release();

        _singleTurnConversation = null;
        _lastConversationUsed = null;
        ModelLoadingState = LMKitModelLoadingState.Unloaded;
        LMKitConfig.LoadedModelUri = null;

        ModelUnloaded?.Invoke(this, new NotifyModelStateChangedEventArgs(unloadedModelUri));
    }

    public async Task<string?> SubmitTranslation(string input, Language language)
    {
        if (_textTranslation == null)
        {
            _textTranslation = new TextTranslation(_model);
        }

        LMKitRequest translationRequest = new LMKitRequest(LMKitRequestType.Translate,
            new TranslationRequestParameters(input, language), LMKitConfig.RequestTimeout);

        _translationsSchedule.Schedule(translationRequest);

        if (_translationsSchedule.Count > 1)
        {
            translationRequest.CanBeExecutedSignal.WaitOne();
        }

        var result = await HandleTranslationRequest(translationRequest);

        return (string?)result.Result;
    }

    public async Task<LMKitResult> SubmitPrompt(Conversation conversation, string prompt)
    {
        var promptRequest = new LMKitRequest(LMKitRequestType.Prompt,
            new LMKitPromptRequestParameters(conversation, prompt),
            LMKitConfig.RequestTimeout);

        _promptSchedule.Schedule(promptRequest);

        if (_promptSchedule.Count > 1)
        {
            promptRequest.CanBeExecutedSignal.WaitOne();
        }

        return await HandlePromptRequest(promptRequest);
    }

    public async Task<PromptResult> RegenerateResponse(Conversation conversation)
    {
        //var message = conversation.ChatHistory.Messages.Last();

        //var regeneratePromptRequest = new RegenerateResponseRequest(string.Empty, LMKitConfig.RequestTimeout);

        //_promptSchedule.Schedule()
        return null;
    }

    public async Task CancelPrompt(Conversation conversation, bool shouldAwaitTermination = false)
    {
        var conversationPrompt = _promptSchedule.Unschedule(conversation);

        if (conversationPrompt != null)
        {
            conversationPrompt.CancellationTokenSource.Cancel();
            conversationPrompt.ResponseTask.TrySetCanceled();

            if (shouldAwaitTermination)
            {
                await conversationPrompt.ResponseTask.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private async Task<LMKitResult> HandleTranslationRequest(LMKitRequest translationRequest)
    {
        // Ensuring we don't touch anything until Lm-Kit objects' state has been set to handle this prompt request.
        _lmKitServiceSemaphore.Wait();

        LMKitResult translationResult;

        if (translationRequest.CancellationTokenSource.IsCancellationRequested || ModelLoadingState == LMKitModelLoadingState.Unloaded)
        {
            translationResult = new LMKitResult()
            {
                Status = LMKitTextGenerationStatus.Cancelled
            };

            _lmKitServiceSemaphore.Release();
        }
        else
        {
            _lmKitServiceSemaphore.Release();

            translationResult = await SubmitTranslationRequest(translationRequest);
        }

        if (_translationsSchedule.Contains(translationRequest))
        {
            _translationsSchedule.Remove(translationRequest);
        }

        translationRequest.ResponseTask.TrySetResult(translationResult);

        return translationResult;

    }

    private async Task<LMKitResult> HandlePromptRequest(LMKitRequest promptRequest)
    {
        // Ensuring we don't touch anything until Lm-Kit objects' state has been set to handle this prompt request.
        _lmKitServiceSemaphore.Wait();

        LMKitResult promptResult;

        if (promptRequest.CancellationTokenSource.IsCancellationRequested || ModelLoadingState == LMKitModelLoadingState.Unloaded)
        {
            promptResult = new LMKitResult()
            {
                Status = LMKitTextGenerationStatus.Cancelled
            };

            _lmKitServiceSemaphore.Release();
        }
        else
        {
            BeforeSubmittingPrompt(((LMKitPromptRequestParameters)promptRequest.Parameters!).Conversation);
            _lmKitServiceSemaphore.Release();

            promptResult = await SubmitPromptRequest(promptRequest);
        }

        if (_promptSchedule.Contains(promptRequest))
        {
            _promptSchedule.Remove(promptRequest);
        }

        promptRequest.ResponseTask.TrySetResult(promptResult);

        return promptResult;

    }

    private async Task<LMKitResult> SubmitPromptRequest(LMKitRequest promptRequest)
    {
        LMKitPromptRequestParameters parameter = (promptRequest.Parameters as LMKitPromptRequestParameters)!;

        try
        {
            _promptSchedule.RunningPromptRequest = promptRequest;

            var result = new LMKitResult();

            try
            {
                result.Result = await _multiTurnConversation!.SubmitAsync(parameter.Prompt,
                    promptRequest.CancellationTokenSource.Token);
            }
            catch (Exception exception)
            {
                result.Exception = exception;

                if (result.Exception is OperationCanceledException)
                {
                    result.Status = LMKitTextGenerationStatus.Cancelled;
                }
                else
                {
                    result.Status = LMKitTextGenerationStatus.UnknownError;
                }
            }

            if (_multiTurnConversation != null)
            {
                parameter.Conversation.ChatHistory = _multiTurnConversation.ChatHistory;
                parameter.Conversation.LatestChatHistoryData = _multiTurnConversation.ChatHistory.Serialize();

                if (parameter.Conversation.GeneratedTitleSummary == null && result.Status == LMKitTextGenerationStatus.Undefined && !string.IsNullOrEmpty(((TextGenerationResult)result.Result!).Completion))
                {
                    GenerateConversationSummaryTitle(parameter.Conversation, parameter.Prompt);
                }
            }

            if (result.Exception != null && promptRequest.CancellationTokenSource.IsCancellationRequested)
            {
                result.Status = LMKitTextGenerationStatus.Cancelled;
            }

            return result;
        }
        catch (Exception exception)
        {
            return new LMKitResult()
            {
                Exception = exception,
                Status = LMKitTextGenerationStatus.UnknownError
            };
        }
        finally
        {
            _promptSchedule.RunningPromptRequest = null;
        }
    }

    private async Task<LMKitResult> SubmitTranslationRequest(LMKitRequest translationRequest)
    {
        TranslationRequestParameters parameters = ((TranslationRequestParameters)translationRequest.Parameters!);

        try
        {
            _translationsSchedule.RunningPromptRequest = translationRequest;

            var result = new LMKitResult();

            try
            {
                result.Result = await _textTranslation!.TranslateAsync(parameters.InputText,
                    parameters.Language, translationRequest.CancellationTokenSource.Token);
            }
            catch (Exception exception)
            {
                result.Exception = exception;

                if (result.Exception is OperationCanceledException)
                {
                    result.Status = LMKitTextGenerationStatus.Cancelled;
                }
                else
                {
                    result.Status = LMKitTextGenerationStatus.UnknownError;
                }
            }


            if (result.Exception != null && translationRequest.CancellationTokenSource.IsCancellationRequested)
            {
                result.Status = LMKitTextGenerationStatus.Cancelled;
            }

            return result;
        }
        catch (Exception exception)
        {
            return new LMKitResult()
            {
                Exception = exception,
                Status = LMKitTextGenerationStatus.UnknownError
            };
        }
        finally
        {
            _promptSchedule.RunningPromptRequest = null;
        }
    }

    private void GenerateConversationSummaryTitle(Conversation conversation, string prompt)
    {
        LMKitRequest titleGenerationRequest = new LMKitRequest(LMKitRequestType.GenerateTitle,
            new LMKitPromptRequestParameters(conversation, prompt), 60);

        _titleGenerationSchedule.Schedule(titleGenerationRequest);

        if (_titleGenerationSchedule.Count > 1)
        {
            titleGenerationRequest.CanBeExecutedSignal.WaitOne();
        }

        _titleGenerationSchedule.RunningPromptRequest = titleGenerationRequest;

        Task.Run(async () =>
        {
            LMKitResult promptResult = new LMKitResult();

            try
            {
                string titleSummaryPrompt = $"What is the topic of the following sentence: {prompt}";

                promptResult.Result = await _singleTurnConversation!.SubmitAsync(titleSummaryPrompt, titleGenerationRequest.CancellationTokenSource.Token);
            }
            catch (Exception exception)
            {
                promptResult.Exception = exception;
            }
            finally
            {
                _titleGenerationSchedule.RunningPromptRequest = null;
                _titleGenerationSchedule.Remove(titleGenerationRequest);
                conversation.SetGeneratedTitle(promptResult);
                titleGenerationRequest.ResponseTask.SetResult(promptResult);
            }
        });
    }

    private void BeforeSubmittingPrompt(Conversation conversation)
    {
        bool conversationIsInitialized = conversation == _lastConversationUsed;

        if (!conversationIsInitialized)
        {
            if (_multiTurnConversation != null)
            {
                _multiTurnConversation.Dispose();
                _multiTurnConversation = null;
            }

            // Latest chat history of this conversation was generated with a different model
            bool lastUsedDifferentModel = LMKitConfig.LoadedModelUri != conversation.LastUsedModelUri;
            bool shouldUseCurrentChatHistory = !lastUsedDifferentModel && conversation.ChatHistory != null;
            bool shouldDeserializeChatHistoryData = (lastUsedDifferentModel && conversation.LatestChatHistoryData != null) || (!lastUsedDifferentModel && conversation.ChatHistory == null);

            if (shouldUseCurrentChatHistory || shouldDeserializeChatHistoryData)
            {
                ChatHistory? chatHistory = shouldUseCurrentChatHistory ? conversation.ChatHistory : ChatHistory.Deserialize(conversation.LatestChatHistoryData, _model);

                _multiTurnConversation = new MultiTurnConversation(_model, chatHistory, LMKitConfig.ContextSize)
                {
                    SamplingMode = GetTokenSampling(LMKitConfig),
                    MaximumCompletionTokens = LMKitConfig.MaximumCompletionTokens,
                };
            }
            else
            {
                _multiTurnConversation = new MultiTurnConversation(_model, LMKitConfig.ContextSize)
                {
                    SamplingMode = GetTokenSampling(LMKitConfig),
                    MaximumCompletionTokens = LMKitConfig.MaximumCompletionTokens,
                    SystemPrompt = LMKitConfig.SystemPrompt
                };
            }

            conversation.ChatHistory = _multiTurnConversation.ChatHistory;
            conversation.LastUsedModelUri = LMKitConfig.LoadedModelUri;
            _lastConversationUsed = conversation;
        }
        else //updating sampling options, if any.
        {
            //todo: Implement a mechanism to determine whether SamplingMode and MaximumCompletionTokens need to be updated.
            _multiTurnConversation.SamplingMode = GetTokenSampling(LMKitConfig);
            _multiTurnConversation.MaximumCompletionTokens = LMKitConfig.MaximumCompletionTokens;

            if (LMKitConfig.ContextSize != _multiTurnConversation.ContextSize)
            {
                //todo: implement context size update.
            }
        }

        if (_singleTurnConversation == null)
        {
            _singleTurnConversation = new SingleTurnConversation(_model)
            {
                MaximumContextLength = 512,
                MaximumCompletionTokens = 50,
                SamplingMode = new GreedyDecoding(),
                SystemPrompt = "You receive a sentence. You are to summarize, with a single sentence containing a maximum of 10 words, the topic of this sentence. You start your answer with 'topic:'"
                //SystemPrompt = "You receive one question and one response taken from a conversation, and you are to provide, with a maximum of 10 words, a summary of the conversation topic."
            };
        }
    }

    private bool OnModelLoadingProgressed(float progress)
    {
        ModelLoadingProgressed?.Invoke(this, new ModelLoadingProgressedEventArgs(_currentlyLoadingModelUri!, progress));

        return true;
    }

    private static TokenSampling GetTokenSampling(LMKitConfig config)
    {
        switch (config.SamplingMode)
        {
            default:
            case SamplingMode.Random:
                return new RandomSampling()
                {
                    Temperature = config.RandomSamplingConfig.Temperature,
                    DynamicTemperatureRange = config.RandomSamplingConfig.DynamicTemperatureRange,
                    TopP = config.RandomSamplingConfig.TopP,
                    TopK = config.RandomSamplingConfig.TopK,
                    MinP = config.RandomSamplingConfig.MinP,
                    LocallyTypical = config.RandomSamplingConfig.LocallyTypical
                };

            case SamplingMode.Greedy:
                return new GreedyDecoding();

            case SamplingMode.Mirostat2:
                return new Mirostat2Sampling()
                {
                    Temperature = config.Mirostat2SamplingConfig.Temperature,
                    LearningRate = config.Mirostat2SamplingConfig.LearningRate,
                    TargetEntropy = config.Mirostat2SamplingConfig.TargetEntropy
                };
        }
    }

    private sealed class PromptSchedule
    {
        private readonly object _locker = new object();

        private List<LMKitRequest> _scheduledPrompts = new List<LMKitRequest>();

        public int Count
        {
            get
            {
                lock (_locker)
                {
                    return _scheduledPrompts.Count;
                }
            }
        }

        public LMKitRequest? Next
        {
            get
            {
                lock (_locker)
                {
                    if (_scheduledPrompts.Count > 0)
                    {
                        var scheduledPrompt = _scheduledPrompts[0];

                        return scheduledPrompt;
                    }

                    return null;
                }
            }
        }

        public LMKitRequest? RunningPromptRequest { get; set; }

        public void Schedule(LMKitRequest promptRequest)
        {
            lock (_locker)
            {
                _scheduledPrompts.Add(promptRequest);

                if (Count == 1)
                {
                    promptRequest.CanBeExecutedSignal.Set();
                }
            }
        }

        public bool Contains(LMKitRequest scheduledPrompt)
        {
            lock (_locker)
            {
                return _scheduledPrompts.Contains(scheduledPrompt);
            }
        }

        public void Remove(LMKitRequest scheduledPrompt)
        {
            lock (_locker)
            {
                HandleScheduledPromptRemoval(scheduledPrompt);
            }
        }

        public LMKitRequest? Unschedule(Conversation conversation)
        {
            LMKitRequest? prompt = null;

            lock (_locker)
            {
                foreach (var scheduledPrompt in _scheduledPrompts)
                {
                    if (scheduledPrompt.Parameters is LMKitPromptRequestParameters parameter && parameter.Conversation == conversation)
                    {
                        prompt = scheduledPrompt;
                        break;
                    }
                }

                if (prompt != null)
                {
                    HandleScheduledPromptRemoval(prompt);
                }
            }

            return prompt;
        }

        private void HandleScheduledPromptRemoval(LMKitRequest scheduledPrompt)
        {
            bool wasFirstInLine = scheduledPrompt == _scheduledPrompts[0];

            _scheduledPrompts.Remove(scheduledPrompt);

            if (wasFirstInLine && Next != null)
            {
                Next.CanBeExecutedSignal.Set();
            }
            else
            {
                scheduledPrompt.CanBeExecutedSignal.Set();
            }
        }
    }

    private sealed class PromptRequest : LMKitPromptRequestBase
    {
        public PromptRequest(Conversation conversation, string prompt, int requestTimeout) : base(conversation, prompt, requestTimeout)
        {
        }
    }

    private sealed class TitleGenerationRequest : LMKitPromptRequestBase
    {
        public string Response { get; }

        public TitleGenerationRequest(Conversation conversation, string prompt, string response, int requestTimeout) : base(conversation, prompt, requestTimeout)
        {
            Response = response;
        }
    }

    private sealed class TranslationRequest : LMKitRequestBase
    {
        public TaskCompletionSource<TranslationResult> TranslationTask { get; } = new TaskCompletionSource<TranslationResult>();

        public TranslationRequest(string prompt, int requestTimeout) : base(prompt, requestTimeout)
        {
        }

        protected override void AwaitResult()
        {
            TranslationTask.Task.Wait();
        }
    }

    private sealed class LMKitRequest
    {
        public ManualResetEvent CanBeExecutedSignal { get; } = new ManualResetEvent(false);
        public CancellationTokenSource CancellationTokenSource { get; }
        public TaskCompletionSource<LMKitResult> ResponseTask { get; } = new TaskCompletionSource<LMKitResult>();
        public object? Parameters { get; }

        public LMKitRequestType RequestType { get; }

        public LMKitRequest(LMKitRequestType requestType, object? parameter, int requestTimeout)
        {
            RequestType = requestType;
            Parameters = parameter;
            CancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeout));
        }

        public void CancelAndAwaitTermination()
        {
            CancellationTokenSource.Cancel();
            ResponseTask.Task.Wait();
        }
    }

    private enum LMKitRequestType
    {
        Prompt,
        RegenerateResponse,
        GenerateTitle,
        Translate
    }

    private sealed class LMKitPromptRequestParameters
    {
        public Conversation Conversation { get; set; }

        public string Prompt { get; set; }

        public LMKitPromptRequestParameters(Conversation conversation, string prompt)
        {
            Conversation = conversation;
            Prompt = prompt;
        }
    }

    private sealed class TranslationRequestParameters
    {
        public string InputText { get; set; }

        public Language Language { get; set; }

        public TranslationRequestParameters(string inputText, Language language)
        {
            InputText = inputText;
            Language = language;
        }
    }

    private abstract class LMKitRequestBase
    {
        public string Prompt { get; }

        public ManualResetEvent CanBeExecutedSignal { get; } = new ManualResetEvent(false);

        public CancellationTokenSource CancellationTokenSource { get; }

        public void CancelAndAwaitTermination()
        {
            CancellationTokenSource.Cancel();
            AwaitResult();
        }

        protected abstract void AwaitResult();

        protected LMKitRequestBase(string prompt, int requestTimeout)
        {
            Prompt = prompt;
            CancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeout));
        }
    }

    private abstract class LMKitPromptRequestBase : LMKitRequestBase
    {
        public Conversation Conversation { get; }
        public TaskCompletionSource<PromptResult> PromptResult { get; } = new TaskCompletionSource<PromptResult>();

        protected LMKitPromptRequestBase(Conversation conversation, string prompt, int requestTimeout) : base(prompt, requestTimeout)
        {
            Conversation = conversation;
        }

        protected override void AwaitResult()
        {
            PromptResult.Task.Wait();
        }
    }

    public class NotifyModelStateChangedEventArgs : EventArgs
    {
        public Uri FileUri { get; }

        public NotifyModelStateChangedEventArgs(Uri fileUri)
        {
            FileUri = fileUri;
        }
    }

    public sealed class ModelLoadingProgressedEventArgs : NotifyModelStateChangedEventArgs
    {
        public double Progress { get; }

        public ModelLoadingProgressedEventArgs(Uri fileUri, double progress) : base(fileUri)
        {
            Progress = progress;
        }
    }

    public sealed class ModelLoadingFailedEventArgs : NotifyModelStateChangedEventArgs
    {
        public Exception Exception { get; }

        public ModelLoadingFailedEventArgs(Uri fileUri, Exception exception) : base(fileUri)
        {
            Exception = exception;
        }
    }

    public sealed class PromptResult
    {
        public Exception? Exception { get; set; }

        public LMKitTextGenerationStatus Status { get; set; }

        public TextGenerationResult? TextGenerationResult { get; set; }
    }

    public sealed class LMKitResult
    {
        public Exception? Exception { get; set; }

        public LMKitTextGenerationStatus Status { get; set; }

        public object? Result { get; set; }
    }

    public sealed class TranslationResult
    {
        public Exception? Exception { get; set; }

        public string? Result { get; set; }

        public LMKitTextGenerationStatus Status { get; set; }
    }


}
