﻿namespace LMKit.Maestro.Services;

public static class LMKitDefaultSettings
{
    public static readonly string DefaultModelStorageDirectory = Global.Configuration.ModelStorageDirectory;

    public const string DefaultSystemPrompt = "You are a chatbot that always responds promptly and helpfully to user requests.";
    public const int DefaultMaximumCompletionTokens = 2048; // TODO: Evan, consider setting this to -1 to indicate no limitation. Ensure the option to configure the chat with a predefined limit remains available.
    public static readonly int DefaultContextSize = Graphics.DeviceConfiguration.GetOptimalContextSize();
    public const int DefaultRequestTimeout = 120;
    public const SamplingMode DefaultSamplingMode = SamplingMode.Random;

    public static SamplingMode[] AvailableSamplingModes { get; } = (SamplingMode[])Enum.GetValues(typeof(SamplingMode));

    public const float DefaultRandomSamplingTemperature = 0.8f;
    public const float DefaultRandomSamplingDynamicTemperatureRange = 0f;
    public const float DefaultRandomSamplingTopP = 0.95f;
    public const float DefaultRandomSamplingMinP = 0.05f;
    public const int DefaultRandomSamplingTopK = 40;
    public const float DefaultRandomSamplingLocallyTypical = 1;

    public const float DefaultMirostat2SamplingTemperature = 0.8f;
    public const float DefaultMirostat2SamplingTargetEntropy = 5.0f;
    public const float DefaultMirostat2SamplingLearningRate = 0.1f;
}