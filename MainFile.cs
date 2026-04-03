using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace SovereignBitemod.SovereignBitemodCode
{
	[ModInitializer(nameof(Initialize))]
	public partial class MainFile : Node
	{
		public const string ModId = "SovereignBitemod";

		// sts2底层日志
		public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } = new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

		// 简易Log
		public static void Log(string message)
		{
			Logger.Info(message);
		}

		public static void Initialize()
		{
			// 启动 Harmony 补丁, 让 SovereignBitePatch.cs 里的反射和 OnPlay 拦截生效
			Harmony harmony = new(ModId);
			harmony.PatchAll();

			Log("[锻蛇] SovereignBitemod 成功加载!");
		}
	}
}
