﻿using LMKit.Maestro.Controls;

namespace LMKit.Maestro.Handlers;

public static class MauiHandlerCollectionExtensions
{
    public static IMauiHandlersCollection AddCustomHandlers(this IMauiHandlersCollection handlers)
    {
        handlers.AddHandler(typeof(CustomEntry), typeof(CustomEntryHandler));
        handlers.AddHandler(typeof(CustomEditor), typeof(CustomEditorHandler));
        handlers.AddHandler(typeof(StatefulContentView), typeof(StatefulContentViewHandler));

        return handlers;
    }
}
