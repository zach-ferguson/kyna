﻿//using Kyna.Infrastructure.Abstractions;
//using Microsoft.Extensions.Logging;

//namespace Kyna.Infrastructure.Logging;

//// TODO: This is only used at the highest level - can probably move up there.
//public static class EventIdRepository
//{
//    private enum Events
//    {
//        None = 0,
//        AppStarted = 100,
//        AppFinished = 900
//    }

//    public static EventId GetAppStartedEvent(IAppConfig appConfig)
//    {
//        if (appConfig != null)
//        {
//            return new EventId((int)Events.AppStarted, appConfig.AppName);
//        }

//        return default;
//    }

//    public static EventId GetAppFinishedEvent(IAppConfig? appConfig)
//    {
//        if (appConfig != null)
//        {
//            return new EventId((int)Events.AppFinished, appConfig.AppName);
//        }

//        return default;
//    }
//}
