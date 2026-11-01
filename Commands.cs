using System;
using System.Linq;
using System.Text;
using CommandSystem;
using LabApi.Features.Wrappers;

namespace LambdaEvents
{
    internal enum AccessLevel
    {
        /// <summary>Только админы (право из конфига) или UserId из AllowedUserIds.</summary>
        Staff,

        /// <summary>Админы или помощники ивента.</summary>
        Helper
    }

    internal static class Access
    {
        public static bool IsStaff(Player player)
        {
            PluginConfig cfg = LambdaEventsPlugin.Instance.Config;

            PlayerPermissions permission;
            if (!Enum.TryParse(cfg.RequiredPermission, true, out permission))
            {
                permission = PlayerPermissions.RoundEvents;
            }

            return player.HasPermission(permission) || cfg.AllowedUserIds.Contains(player.UserId);
        }

        /// <param name="actor">Игрок, выполняющий команду; null - консоль сервера (доступ всегда есть).</param>
        public static bool Allowed(Player actor, AccessLevel level)
        {
            if (actor == null)
            {
                return true;
            }

            if (IsStaff(actor))
            {
                return true;
            }

            return level == AccessLevel.Helper && EventService.IsHelper(actor.UserId);
        }
    }

    /// <summary>Общая логика: проверка плагина и прав, затем выполнение.</summary>
    public abstract class EventCommandBase : ICommand
    {
        public abstract string Command { get; }
        public virtual string[] Aliases => Array.Empty<string>();
        public abstract string Description { get; }

        internal abstract AccessLevel Required { get; }

        internal abstract bool Run(string[] args, Player actor, out string response);

        public bool Execute(ArraySegment<string> arguments, ICommandSender sender, out string response)
        {
            if (LambdaEventsPlugin.Instance == null)
            {
                response = "Плагин Lambda Events выключен.";
                return false;
            }

            Player actor;
            Player.TryGet(sender, out actor);

            if (!Access.Allowed(actor, Required))
            {
                response = "Нет доступа. Нужны права на ивенты или статус помощника ивента.";
                return false;
            }

            return Run(arguments.ToArray(), actor, out response);
        }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class EvPodCommand : EventCommandBase
    {
        public override string Command => "evpod";
        public override string Description => "Подготовка к ивенту: evpod <RP> <название ивента>";
        internal override AccessLevel Required => AccessLevel.Staff;

        internal override bool Run(string[] args, Player actor, out string response)
        {
            if (args.Length < 2)
            {
                response = "Использование: evpod <RP> <название ивента>\nПример: evpod рп Побег из комплекса";
                return false;
            }

            if (EventService.State != EventState.Idle)
            {
                response = $"Ивент «{EventService.EventName}» уже активен. Сначала завершите его командой evstop.";
                return false;
            }

            string rp = args[0];
            string name = string.Join(" ", args.Skip(1));

            EventService.Prepare(rp, name);
            response = $"Подготовка к ивенту «{name}» ({rp}) началась. Название сервера: {EventService.Format(LambdaEventsPlugin.Instance.Config.PreparingNameFormat)}";
            return true;
        }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class EvStartCommand : EventCommandBase
    {
        public override string Command => "evstart";
        public override string Description => "Начать ивент (после evpod).";

        internal override AccessLevel Required =>
            LambdaEventsPlugin.Instance != null && LambdaEventsPlugin.Instance.Config.HelpersCanControlEvent
                ? AccessLevel.Helper
                : AccessLevel.Staff;

        internal override bool Run(string[] args, Player actor, out string response)
        {
            if (EventService.State == EventState.Idle)
            {
                response = "Нет подготовленного ивента. Сначала используйте evpod <RP> <название>.";
                return false;
            }

            if (EventService.State == EventState.Running)
            {
                response = $"Ивент «{EventService.EventName}» уже идёт.";
                return false;
            }

            EventService.Start();
            response = $"Ивент «{EventService.EventName}» ({EventService.RpType}) начался!";
            return true;
        }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class EvStopCommand : EventCommandBase
    {
        public override string Command => "evstop";
        public override string Description => "Завершить ивент и вернуть название сервера.";

        internal override AccessLevel Required =>
            LambdaEventsPlugin.Instance != null && LambdaEventsPlugin.Instance.Config.HelpersCanControlEvent
                ? AccessLevel.Helper
                : AccessLevel.Staff;

        internal override bool Run(string[] args, Player actor, out string response)
        {
            if (EventService.State == EventState.Idle)
            {
                response = "Сейчас нет активного ивента.";
                return false;
            }

            string name = EventService.EventName;
            EventService.Stop();
            response = $"Ивент «{name}» завершён. Название сервера возвращено.";
            return true;
        }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class EvListCommand : EventCommandBase
    {
        public override string Command => "evlist";
        public override string Description => "Статус ивента и список помощников.";
        internal override AccessLevel Required => AccessLevel.Helper;

        internal override bool Run(string[] args, Player actor, out string response)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Lambda Events ===");

            switch (EventService.State)
            {
                case EventState.Idle:
                    sb.AppendLine("Состояние: нет активного ивента");
                    break;

                case EventState.Preparing:
                    sb.AppendLine("Состояние: подготовка");
                    sb.AppendLine($"Ивент: {EventService.EventName} ({EventService.RpType})");
                    sb.AppendLine($"Подготовка идёт: {FormatSpan(DateTime.UtcNow - EventService.PreparedAt)}");
                    break;

                case EventState.Running:
                    sb.AppendLine("Состояние: ивент идёт");
                    sb.AppendLine($"Ивент: {EventService.EventName} ({EventService.RpType})");
                    sb.AppendLine($"Идёт уже: {FormatSpan(DateTime.UtcNow - EventService.StartedAt)}");
                    break;
            }

            sb.AppendLine($"Игроков онлайн: {Player.ReadyList.Count(p => p.IsPlayer)}");
            sb.AppendLine($"Помощники ({EventService.Helpers.Count}):");

            if (EventService.Helpers.Count == 0)
            {
                sb.Append("  нет");
            }
            else
            {
                foreach (string userId in EventService.Helpers)
                {
                    Player online = Player.ReadyList.FirstOrDefault(p => p.IsPlayer && p.UserId == userId);
                    if (online != null)
                    {
                        sb.AppendLine($"  [{online.PlayerId}] {online.Nickname} - онлайн");
                    }
                    else
                    {
                        sb.AppendLine($"  {userId} - не в сети");
                    }
                }
            }

            response = sb.ToString().TrimEnd();
            return true;
        }

        private static string FormatSpan(TimeSpan span)
        {
            return $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
        }
    }

    [CommandHandler(typeof(RemoteAdminCommandHandler))]
    [CommandHandler(typeof(GameConsoleCommandHandler))]
    [CommandHandler(typeof(ClientCommandHandler))]
    public sealed class AddHelperCommand : EventCommandBase
    {
        public override string Command => "addhelper";
        public override string Description => "Назначить помощника ивента: addhelper <PlayerID>";
        internal override AccessLevel Required => AccessLevel.Staff;

        internal override bool Run(string[] args, Player actor, out string response)
        {
            if (args.Length < 1)
            {
                response = "Использование: addhelper <PlayerID>\nID игрока виден в списке игроков Remote Admin.";
                return false;
            }

            Player target = null;
            int playerId;

            if (int.TryParse(args[0], out playerId))
            {
                target = Player.Get(playerId);
            }
            else if (args[0].Contains("@"))
            {
                target = Player.Get(args[0]);
            }

            if (target == null || !target.IsPlayer || !target.IsReady)
            {
                response = $"Игрок «{args[0]}» не найден. Укажите числовой PlayerID.";
                return false;
            }

            if (!EventService.AddHelper(target))
            {
                response = $"{target.Nickname} [{target.PlayerId}] уже помощник ивента.";
                return false;
            }

            string message = LambdaEventsPlugin.Instance.Config.HelperMessage;
            if (!string.IsNullOrWhiteSpace(message))
            {
                target.SendBroadcast(message, LambdaEventsPlugin.Instance.Config.BroadcastDuration);
            }

            response = $"{target.Nickname} [{target.PlayerId}] назначен помощником ивента.";
            return true;
        }
    }
}
