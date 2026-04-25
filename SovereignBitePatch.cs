using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.ValueProps;
using System.Reflection;
using static Godot.Performance;


namespace SovereignBitemod.SovereignBitemodCode
{
    [HarmonyPatch]
    public static class SovereignBitePatch
    {
        // 复刻官方 GetVfxNode, 利用节点树动态查找蛇咬的专属大剑
        public static NSovereignBladeVfx? GetSnakebiteVfxNode(Player player, Snakebite card)
        {
            CardModel originalCard = card.DupeOf ?? card;
            return (NCombatRoom.Instance?.GetCreatureNode(player.Creature))?
                .GetChildren()
                .OfType<NSovereignBladeVfx>()
                .FirstOrDefault(b => b.Card == originalCard || b.Card == card);
        }

        [HarmonyPatch(typeof(ForgeCmd), "Forge")]
        [HarmonyPrefix]
        public static bool Prefix_Forge(decimal amount, Player player, AbstractModel source, ref Task<IEnumerable<SovereignBlade>> __result)
        {
            __result = ExecuteForgeAsync(amount, player, source);
            return false; // 拦截成功
        }

        private static async Task<IEnumerable<SovereignBlade>> ExecuteForgeAsync(decimal amount, Player player, AbstractModel source)
        {
            IEnumerable<SovereignBlade> emptyResult = Array.Empty<SovereignBlade>();
            if (CombatManager.Instance.IsOverOrEnding) return emptyResult;

            var allSnakebites = player.PlayerCombatState.AllCards.OfType<Snakebite>().ToList();

            Snakebite activeSnake = null;
            if (allSnakebites.Count == 0)
            {
                Snakebite newSnakebite = player.Creature.CombatState.CreateCard<Snakebite>(player);
                await CardPileCmd.AddGeneratedCardToCombat(newSnakebite, PileType.Hand, creator: player);
                allSnakebites.Add(newSnakebite);
                activeSnake = newSnakebite;
            }
            else
            {
                activeSnake = allSnakebites.FirstOrDefault(c => c.Pile != null && c.Pile.Type == PileType.Hand) ?? allSnakebites.First();
            }

            if (activeSnake != null)
            {
                var vfxNode = GetSnakebiteVfxNode(player, activeSnake);
                if (vfxNode == null)
                {
                    vfxNode = NSovereignBladeVfx.Create(activeSnake);
                    NCreature playerNode = NCombatRoom.Instance?.GetCreatureNode(player.Creature);
                    if (vfxNode != null && playerNode != null)
                    {
                        playerNode.CallDeferred("add_child", vfxNode);
                        vfxNode.CallDeferred("Forge", (float)amount, true);
                        MainFile.Log("[锻蛇] 蛇咬大剑已成功挂载在玩家节点下");
                    }
                }
                else
                {
                    vfxNode.Forge((float)activeSnake.DynamicVars.Poison.BaseValue, showFlames: true);
                }
            }

            // 处理手牌里的蛇咬特效
            List<Snakebite> handSnakes = allSnakebites.Where(c => c.Pile != null && c.Pile.Type == PileType.Hand).ToList();
            List<Snakebite> otherSnakes = allSnakebites.Where(c => c.Pile != null && c.Pile.Type != PileType.Hand).ToList();

            foreach (var snake in handSnakes)
            {
                var handCardNode = NCombatRoom.Instance?.Ui?.Hand?.GetCard(snake);
                if (handCardNode != null)
                {
                    NCardSmithVfx child = NCardSmithVfx.Create(handCardNode, playSfx: false);
                    NRun.Instance.GlobalUi.AboveTopBarVfxContainer.AddChildSafely(child);
                }
            }

            if (otherSnakes.Count != 0)
            {
                NCardSmithVfx child2 = NCardSmithVfx.Create(otherSnakes, playSfx: false);
                NRun.Instance.GlobalUi.CardPreviewContainer.AddChildSafely(child2);
            }

            foreach (Snakebite snakebite in allSnakebites)
            {
                snakebite.DynamicVars.Poison.BaseValue += amount;
            }

            if (activeSnake != null)
            {
                var nSovereignBladeVfx = GetSnakebiteVfxNode(player, activeSnake);
                if (nSovereignBladeVfx == null)
                {
                    SfxCmd.Play("event:/sfx/characters/regent/regent_forge");
                }
                else
                {
                    SfxCmd.Play("event:/sfx/characters/regent/regent_refine");
                }
            }

            await Hook.AfterForge(player.Creature.CombatState, amount, player, source);
            return emptyResult;
        }
    }

    // 重构: 打出蛇咬
    [HarmonyPatch(typeof(Snakebite), "OnPlay")]
    public static class Snakebite_OnPlay_Patch
    {
        [HarmonyPrefix]
        public static bool Prefix(Snakebite __instance, PlayerChoiceContext choiceContext, CardPlay cardPlay, ref Task __result)
        {
            // 拦截所有打出逻辑, 接管异步链条
            __result = PlaySnakebiteAsync(__instance, choiceContext, cardPlay);
            return false;
        }

        private static async Task PlaySnakebiteAsync(Snakebite instance, PlayerChoiceContext choiceContext, CardPlay cardPlay)
        {
            // 触发施法动画
            await CreatureCmd.TriggerAnim(instance.Owner.Creature, "Cast", instance.Owner.Character.CastAnimDelay);

            // 检测飞剑, 没有就生成
            var vfxNode = SovereignBitePatch.GetSnakebiteVfxNode(instance.Owner, instance);
            bool isFirstTimePlay = false;

            if (vfxNode == null)
            {
                isFirstTimePlay = true;
                MainFile.Log("[锻蛇] 检测到有蛇咬第一次打出");

                vfxNode = NSovereignBladeVfx.Create(instance);
                NCreature playerNode = NCombatRoom.Instance?.GetCreatureNode(instance.Owner.Creature);

                if (vfxNode != null && playerNode != null)
                {
                    playerNode.CallDeferred("add_child", vfxNode);
                    vfxNode.CallDeferred("Forge", (float)instance.DynamicVars.Poison.BaseValue, true);
                    MainFile.Log("[锻蛇] 检测蛇咬但未绑定大剑, 本次打出将生成蛇咬大剑, 但不会有斩击动画");
                }
            }

            // 重构: 剑圣
            int repeats = (int)instance.DynamicVars.Repeat.BaseValue;
            bool hasSeekingEdge = instance.Owner != null && instance.Owner.Creature.HasPower<SeekingEdgePower>();

            // 无论是单体还是AOE，都由同一个循环控制多次打出
            for (int i = 0; i < repeats; i++)
            {
                if (hasSeekingEdge)
                {
                    // AOE: 咬所有人
                    IReadOnlyList<Creature> hittableEnemies = instance.CombatState.HittableEnemies;
                    if (hittableEnemies != null && hittableEnemies.Count > 0)
                    {
                        foreach (var monster in hittableEnemies)
                        {
                            VfxCmd.PlayOnCreatureCenter(monster, "vfx/vfx_bite");
                            await PowerCmd.Apply<PoisonPower>(choiceContext, monster, instance.DynamicVars.Poison.BaseValue, instance.Owner.Creature, instance);
                        }
                    }
                }
                else
                {
                    if (cardPlay.Target != null)
                    {
                        VfxCmd.PlayOnCreatureCenter(cardPlay.Target, "vfx/vfx_bite");
                        await PowerCmd.Apply<PoisonPower>(choiceContext, cardPlay.Target, instance.DynamicVars.Poison.BaseValue, instance.Owner.Creature, instance);
                    }
                }
            }

            // 招架: 每当你打出蛇咬时, 获得10点格挡
            decimal parryAmount = instance.Owner.Creature.GetPowerAmount<ParryPower>();
            if (parryAmount > 0m)
            {
                // 闪烁能力图标
                instance.Owner.Creature.GetPower<ParryPower>()?.Flash();

                // 计算最终格挡, 会自动计算敏捷, 脆弱
                await CreatureCmd.GainBlock(instance.Owner.Creature, parryAmount, ValueProp.Move, cardPlay);
            }

            // 触发蛇咬大剑 (和君王之剑一样)
            if (!isFirstTimePlay && vfxNode != null)
            {
                Creature targetCreature = null;
                if (hasSeekingEdge)
                {
                    var hittableEnemies = instance.CombatState.HittableEnemies;
                    if (hittableEnemies != null && hittableEnemies.Count > 0) targetCreature = hittableEnemies[0];
                }
                else
                {
                    targetCreature = cardPlay.Target;
                }

                if (targetCreature != null)
                {
                    try
                    {
                        NCreature nCreature = NCombatRoom.Instance?.GetCreatureNode(targetCreature);
                        if (nCreature != null)
                        {
                            // 这里可以修改成别的整活音效 ----------------------------------------------------------------*
                            SfxCmd.Play("event:/sfx/characters/regent/regent_sovereign_blade");
                            vfxNode.Attack(nCreature.VfxSpawnPosition);
                            // 延时二次剑鸣音效
                            _ = Task.Delay(200).ContinueWith(t =>
                            {
                                SfxCmd.Play("event:/sfx/characters/regent/regent_sovereign_blade");
                            });
                        }
                    }
                    catch (Exception e)
                    {
                        MainFile.Log($"[锻蛇] 飞剑斩击失败, 原因: {e.Message}");
                    }
                }
            }
        }
    }

    // 当鼠标放在有"锻造"词条的地方时, 显示蛇咬卡面而不是君王之剑
    [HarmonyPatch(typeof(HoverTipFactory), "FromForge")]
    public static class Patch_FromForge
    {
        [HarmonyPrefix]
        public static bool Prefix(ref IEnumerable<IHoverTip> __result)
        {
            List<IHoverTip> list = new List<IHoverTip>();
            list.AddRange(HoverTipFactory.FromCardWithCardHoverTips<Snakebite>());
            __result = list;
            return false;
        }
    }

    // 绑定蛇咬大剑的贴图
    [HarmonyPatch(typeof(NSovereignBladeVfx), "Create")]
    public static class Patch_OverrideBladeVfx
    {
        [HarmonyPostfix]
        public static void Postfix(CardModel card, ref NSovereignBladeVfx __result)
        {
            if (card is Snakebite && __result != null)
            {
                MainFile.Log("[锻蛇] 寻找动画容器中...");
                Node targetNode = null;
                Node spineSword = __result.FindChild("SpineSword", true, false);
                if (spineSword != null)
                {
                    Node swordBone = spineSword.FindChild("SwordBone", true, false);
                    if (swordBone != null)
                    {
                        targetNode = swordBone.FindChild("ScaleContainer", true, false);
                    }
                }
                if (targetNode == null) targetNode = spineSword;
                if (targetNode == null) return;

                Sprite2D snakeSprite = new Sprite2D();
                try
                {
                    string exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath());
                    string realImagePath = System.IO.Path.Combine(exeDir, "mods", "SovereignBitemod", "assets", "snake_vfx.png");

                    if (System.IO.File.Exists(realImagePath))
                    {
                        byte[] imageBytes = System.IO.File.ReadAllBytes(realImagePath);
                        Image img = new Image();
                        Error err = img.LoadPngFromBuffer(imageBytes);

                        if (err == Error.Ok)
                        {
                            ImageTexture imgTex = ImageTexture.CreateFromImage(img);
                            snakeSprite.Texture = imgTex;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MainFile.Log($"[锻蛇] 加载图片异常: {ex.Message}");
                }

                snakeSprite.RotationDegrees = -90f;
                snakeSprite.Scale = new Vector2(2.0f, 2.0f);
                // X 正数往下偏，负数往上偏；Y 正数往左偏，负数往右偏
                snakeSprite.Position = new Vector2(10.0f, -1000.0f);

                targetNode.AddChild(snakeSprite);
                MainFile.Log($"[锻蛇] 贴图已旋转并成功绑定在 {targetNode.Name} 上！");
            }
        }
    }

    // 征服者: 指定一名敌人在本回合受到蛇咬的毒翻倍
    [HarmonyPatch(typeof(Hook), "ModifyPowerAmountGiven")]
    public static class Patch_DoublePoisonOnConqueror_PreviewAndApply
    {
        [HarmonyPostfix]
        public static void Postfix(ref decimal __result, PowerModel power, Creature giver, decimal amount, Creature? target, CardModel? cardSource)
        {
            if (power != null && power.GetType().Name == "PoisonPower")
            {
                if (target != null && target.Powers != null)
                {
                    var hasConqueror = target.GetPower<ConquerorPower>() != null;
                    if (hasConqueror)
                    {
                        __result *= 2m;
                        MainFile.Log($"[锻蛇] ModifyPowerAmountGiven 成功拦截! 毒修正为: {__result}");
                    }
                }
            }
        }
    }

    // 征召上前: 无论何处, 把蛇咬都放回手牌
    [HarmonyPatch(typeof(SummonForth), "OnPlay")]
    public static class Patch_SummonForth_BringSnakebiteBack
    {
        [HarmonyPrefix]
        public static bool Prefix(SummonForth __instance, PlayerChoiceContext choiceContext, CardPlay cardPlay, ref Task __result)
        {
            __result = ExecuteSummonForthAsync(__instance);
            return false;
        }

        private static async Task ExecuteSummonForthAsync(SummonForth instance)
        {
            await CreatureCmd.TriggerAnim(instance.Owner.Creature, "Cast", instance.Owner.Character.CastAnimDelay);
            await ForgeCmd.Forge(instance.DynamicVars.Forge.IntValue, instance.Owner, instance);

            IEnumerable<Snakebite> enumerable = (from c in instance.Owner.PlayerCombatState.AllCards.OfType<Snakebite>()
                                                 where c.Pile != null && c.Pile.Type != PileType.Hand
                                                 select c).ToList();

            foreach (Snakebite item in enumerable)
            {
                await CardPileCmd.Add(item, PileType.Hand);
            }
        }
    }

    // 传世之锤: 从手牌中的白卡或蛇咬中选择一张复制到手牌
    [HarmonyPatch(typeof(HeirloomHammer), "OnPlay")]
    public static class Patch_HeirloomHammer_IncludeSnakebite
    {
        [HarmonyPrefix]
        public static bool Prefix(HeirloomHammer __instance, PlayerChoiceContext choiceContext, CardPlay cardPlay, ref Task __result)
        {
            __result = PlayHeirloomHammerAsync(__instance, choiceContext, cardPlay);
            return false;
        }

        private static async Task PlayHeirloomHammerAsync(HeirloomHammer instance, PlayerChoiceContext choiceContext, CardPlay cardPlay)
        {
            System.ArgumentNullException.ThrowIfNull(cardPlay.Target, "cardPlay.Target");

            await DamageCmd.Attack(instance.DynamicVars.Damage.BaseValue)
                .FromCard(instance)
                .Targeting(cardPlay.Target)
                .WithHitFx("vfx/vfx_attack_blunt", null, "blunt_attack.mp3")
                .Execute(choiceContext);

            var selectedList = await CardSelectCmd.FromHand(
                prefs: new CardSelectorPrefs(instance.SelectionScreenPrompt, 1),
                context: choiceContext,
                player: instance.Owner,
                filter: (CardModel c) => c.VisualCardPool.IsColorless || c is Snakebite,
                source: instance
            );

            CardModel selection = selectedList?.FirstOrDefault();
            if (selection != null)
            {
                for (int i = 0; i < instance.DynamicVars.Repeat.IntValue; i++)
                {
                    CardModel card = selection.CreateClone();
                    await CardPileCmd.AddGeneratedCardToCombat(card, PileType.Hand, creator: instance.Owner);
                }
            }
        }
    }

    // 君王矿石: 每当你生成一张无色牌或蛇咬时, 获得2点格挡
    [HarmonyPatch(typeof(Regalite), "AfterCardGeneratedForCombat")]
    public static class Patch_Regalite_IncludeSnakebite
    {
        [HarmonyPrefix]
        public static bool Prefix(Regalite __instance, CardModel card, Player? creator, ref Task __result)
        {
            // 拦截逻辑：如果是无色牌（官方原版逻辑）或者是我们的蛇咬
            if (creator != null && creator == __instance.Owner && (card.VisualCardPool.IsColorless || card is Snakebite))
            {
                __result = ExecuteRegaliteAsync(__instance, card);
                return false; // 完全接管，不执行原版，防止双倍格挡
            }
            return true; // 其他情况（虽然基本没其他情况）交给原版
        }

        private static async Task ExecuteRegaliteAsync(Regalite instance, CardModel card)
        {
            instance.Flash();
            await CreatureCmd.GainBlock(instance.Owner.Creature, instance.DynamicVars.Block, null, fast: true);
        }
    }

    // 追踪之刃: 蛇咬现在会对所有敌人施加中毒
    [HarmonyPatch(typeof(CardModel), "get_TargetType")]
    public static class Patch_Snakebite_TargetType_Dynamic
    {
        [HarmonyPostfix]
        public static void Postfix(CardModel __instance, ref TargetType __result)
        {
            if (__instance is Snakebite)
            {
                if (__instance.Owner != null && __instance.Owner.Creature.HasPower<SeekingEdgePower>())
                {
                    __result = TargetType.AllEnemies;
                }
            }
        }
    }

    // 蛇咬生成或被复制的时候, 给它绑定蛇咬大剑的特效
    [HarmonyPatch(typeof(CardPileCmd), "AddGeneratedCardToCombat")]
    public static class Patch_CardGenerated_AttachBlade
    {
        [HarmonyPostfix]
        public static void Postfix(CardModel card, PileType newPileType, Player? creator, CardPilePosition position)
        {
            // 如果新生成的卡牌是蛇咬
            if (card is Snakebite)
            {
                MainFile.Log($"[锻蛇] 检测到新蛇咬卡加入牌堆({newPileType})，正在注入官方的大剑特效...");

                // 找到大剑的玩家节点
                NCreature playerNode = NCombatRoom.Instance?.GetCreatureNode(card.Owner.Creature);
                if (playerNode == null) return;

                // 仿照官方 GetVfxNode 的逻辑
                // 防止重复生成
                CardModel originalCard = card.DupeOf ?? card;
                bool alreadyHasBlade = playerNode.GetChildren()
                    .OfType<NSovereignBladeVfx>()
                    .Any(b => b.Card == originalCard || b.Card == card);

                if (!alreadyHasBlade)
                {
                    // 为新生的蛇咬捏一把飞剑!
                    NSovereignBladeVfx vfxNode = NSovereignBladeVfx.Create(card);
                    if (vfxNode != null)
                    {
                        // 挂载到玩家身上
                        playerNode.CallDeferred("add_child", vfxNode);

                        // 用它当前的数值初始化蛇咬大剑的大小
                        vfxNode.CallDeferred("Forge", (float)card.DynamicVars.Poison.BaseValue, true);

                        MainFile.Log($"[锻蛇] 当前蛇咬({card.GetHashCode()}) 获得了专属的蛇咬大剑! ");
                    }
                }
            }
        }
    }

    // 剑圣: 仿照君王之剑, 强行给蛇咬注入Repeat变量
    [HarmonyPatch(typeof(Snakebite), MethodType.Constructor)]
    public static class Patch_Snakebite_AddRepeatVar
    {
        [HarmonyPostfix]
        public static void Postfix(Snakebite __instance)
        {
            // 利用反射拿到 DynamicVarSet 里的私有字典 _vars
            var varsField = typeof(DynamicVarSet).GetField("_vars", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (varsField != null)
            {
                var varsDict = varsField.GetValue(__instance.DynamicVars) as Dictionary<string, DynamicVar>;
                if (varsDict != null && !varsDict.ContainsKey("Repeat"))
                {
                    varsDict["Repeat"] = new RepeatVar(1);
                    MainFile.Log("[锻蛇] 成功为蛇咬卡牌注入 Repeat 变量");
                }
            }
        }
    }

    // 剑圣: 蛇咬现在会额外命中一次
    [HarmonyPatch(typeof(SwordSagePower), "AfterPowerAmountChanged")] // 打出剑圣后
    public static class Patch_SwordSage_PowerChanged
    {
        [HarmonyPostfix]
        public static void Postfix(SwordSagePower __instance, PowerModel power, decimal amount)
        {
            if (power is SwordSagePower && power.Owner == __instance.Owner)
            {
                IEnumerable<CardModel> enumerable = __instance.Owner.Player?.PlayerCombatState?.AllCards ?? Array.Empty<CardModel>();
                foreach (CardModel item in enumerable)
                {
                    if (item is Snakebite snakebite)
                    {
                        snakebite.DynamicVars.Repeat.BaseValue = (int)(__instance.Amount + 1);
                    }
                }
            }
        }
    }
    [HarmonyPatch(typeof(SwordSagePower), "AfterCardEnteredCombat")] // 兼容后续新生成的蛇咬
    public static class Patch_SwordSage_CardEntered
    {
        [HarmonyPostfix]
        public static void Postfix(SwordSagePower __instance, CardModel card)
        {
            if (card.Owner == __instance.Owner.Player && card is Snakebite snakebite)
            {
                snakebite.DynamicVars.Repeat.BaseValue = (int)(__instance.Amount + 1);
            }
        }
    }

    // 升级后的蛇咬变成1费但是不会再提升数值
    [HarmonyPatch(typeof(Snakebite), "OnUpgrade")]
    public static class Patch_Snakebite_OnUpgrade
    {
        [HarmonyPrefix]
        public static bool Prefix(Snakebite __instance)
        {
            // 拦截蛇咬原版的升级逻辑
            // 不执行任何原版代码

            // 注入费用减1
            __instance.EnergyCost.UpgradeBy(-1);

            // 返回false屏蔽游戏原有逻辑
            return false;
        }
    }
}