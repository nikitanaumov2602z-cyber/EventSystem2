using System;
using System.Collections.Generic;
using System.ComponentModel;
using LabApi.Features;
using LabApi.Features.Console;
using LabApi.Features.Wrappers;
using LabApi.Loader.Features.Plugins;

namespace LambdaEvents
{
    /// <summary>
    /// Плагин для SCP:SL сервера "Lambda Project | Events".
    /// Команды: evpod, evstart, evstop, evlist, addhelper.
    /// </summary>
    public sealed class LambdaEventsPlugin : Plugin<PluginConfig>
    {
        public static LambdaEventsPlugin Instance { get; private set; }

        public override string Name => "Lambda Events";
        public override string Description => "Управление ивентами: evpod, evstart, evstop, evlist, addhelper.";
        public override string Author => "Sam";
        public override Version Version => new Version(1, 0, 0, 0);
        public override Version RequiredApiVersion { get; } = new Version(LabApiProperties.CompiledVersion);

        public override void Enable()
        {
            Instance = this;
            EventService.Reset();
            Logger.Info("Lambda Events включён. Команды: evpod, evstart, evstop, evlist, addhelper.");
        }

        public override void Disable()
        {
            EventService.Shutdown();
            Instance = null;
            Logger.Info("Lambda Events выключен.");
        }
    }

    /// <summary>Конфиг (config.yml). Плейсхолдеры: {base}, {event}, {rp}.</summary>
    public sealed class PluginConfig
    {
        [Description("Базовое название сервера ({base}). Если пусто - берётся название на момент evpod.")]
        public string BaseServerName { get; set; } = "Lambda Project | Events";

        [Description("Название сервера во время подготовки (после evpod).")]
        public string PreparingNameFormat { get; set; } = "{base} Подготовка к ивенту ({event}) ({rp})";

        [Description("Название сервера, когда ивент идёт (после evstart).")]
        public string RunningNameFormat { get; set; } = "{base} Ивент ({event}) ({rp})";

        [Description("Показывать ли броадкасты игрокам.")]
        public bool ShowBroadcasts { get; set; } = true;

        [Description("Длительность броадкаста в секундах.")]
        public ushort BroadcastDuration { get; set; } = 10;

        [Description("Броадкаст при evpod.")]
        public string PrepareBroadcast { get; set; } = "<color=#FFD700><b>Подготовка к ивенту «{event}» ({rp})</b></color>\nОжидайте начала!";

        [Description("Броадкаст при evstart.")]
        public string StartBroadcast { get; set; } = "<color=#00FF66><b>Ивент «{event}» ({rp}) начался!</b></color>";

        [Description("Броадкаст при evstop.")]
        public string StopBroadcast { get; set; } = "<color=#FF5555><b>Ивент «{event}» завершён.</b></color>\nСпасибо за игру!";

        [Description("Сообщение игроку, которого назначили помощником (addhelper).")]
        public string HelperMessage { get; set; } = "<color=#00BFFF>Вы назначены помощником ивента.</color>\nКоманды в консоли (~): .evstart, .evstop, .evlist";

        [Description("Какое право (PlayerPermissions) нужно для управления ивентами. Например: RoundEvents, Broadcasting, ServerConsoleCommands.")]
        public string RequiredPermission { get; set; } = "RoundEvents";

        [Description("UserId (например 76561198000000000@steam), которым разрешено управлять ивентами независимо от прав.")]
        public List<string> AllowedUserIds { get; set; } = new List<string>();

        [Description("Могут ли помощники использовать evstart и evstop.")]
        public bool HelpersCanControlEvent { get; set; } = true;

        [Description("Очищать список помощников после evstop.")]
        public bool ClearHelpersOnStop { get; set; } = true;
    }

    public enum EventState
    {
        Idle,
        Preparing,
        Running
    }

    /// <summary>Состояние текущего ивента и действия над ним.</summary>
    internal static class EventService
    {
        private static string _originalName = string.Empty;

        public static EventState State { get; private set; } = EventState.Idle;
        public static string EventName { get; private set; } = string.Empty;
        public static string RpType { get; private set; } = string.Empty;
        public static DateTime PreparedAt { get; private set; }
        public static DateTime StartedAt { get; private set; }

        /// <summary>UserId помощников.</summary>
        public static readonly HashSet<string> Helpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static PluginConfig Cfg => LambdaEventsPlugin.Instance.Config;

        public static bool IsHelper(string userId) => !string.IsNullOrEmpty(userId) && Helpers.Contains(userId);

        public static bool AddHelper(Player player)
        {
            return Helpers.Add(player.UserId);
        }

        public static void Reset()
        {
            State = EventState.Idle;
            EventName = string.Empty;
            RpType = string.Empty;
            Helpers.Clear();
        }

        public static void Prepare(string rp, string eventName)
        {
            _originalName = Server.ServerListName;
            RpType = rp;
            EventName = eventName;
            PreparedAt = DateTime.UtcNow;
            State = EventState.Preparing;

            Server.ServerListName = Format(Cfg.PreparingNameFormat);
            Announce(Cfg.PrepareBroadcast);
            Logger.Info($"Подготовка к ивенту: {eventName} ({rp})");
        }

        public static void Start()
        {
            StartedAt = DateTime.UtcNow;
            State = EventState.Running;

            Server.ServerListName = Format(Cfg.RunningNameFormat);
            Announce(Cfg.StartBroadcast);
            Logger.Info($"Ивент начался: {EventName} ({RpType})");
        }

        public static void Stop()
        {
            Announce(Cfg.StopBroadcast);
            Logger.Info($"Ивент завершён: {EventName} ({RpType})");

            RestoreName();
            State = EventState.Idle;
            EventName = string.Empty;
            RpType = string.Empty;

            if (Cfg.ClearHelpersOnStop)
            {
                Helpers.Clear();
            }
        }

        /// <summary>Вызывается при выключении плагина: возвращает название сервера.</summary>
        public static void Shutdown()
        {
            if (State != EventState.Idle)
            {
                RestoreName();
            }

            State = EventState.Idle;
            Helpers.Clear();
        }

        public static string BaseName()
        {
            string baseName = Cfg.BaseServerName;
            return string.IsNullOrWhiteSpace(baseName) ? _originalName : baseName;
        }

        public static void Announce(string template)
        {
            if (!Cfg.ShowBroadcasts || string.IsNullOrWhiteSpace(template))
            {
                return;
            }

            Server.SendBroadcast(Format(template), Cfg.BroadcastDuration);
        }

        public static string Format(string template)
        {
            return template
                .Replace("{base}", BaseName())
                .Replace("{event}", EventName)
                .Replace("{rp}", RpType);
        }

        private static void RestoreName()
        {
            Server.ServerListName = BaseName();
        }
    }
}
