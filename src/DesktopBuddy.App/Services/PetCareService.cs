using DesktopBuddy.Models;

namespace DesktopBuddy.Services;

public sealed record ShopItem(string Id, string Name, string Description, int Price,
    int Food = 0, int Water = 0, int Mood = 0, int Health = 0, string Skin = "");
public sealed record PetActivity(string Id, string Name, int Minutes, int Energy, string Reward);

/// <summary>All actions run on a copy and are saved before replacing live state. No network or timers here.</summary>
public static class PetCareService
{
    public static readonly IReadOnlyList<ShopItem> Items = Array.AsReadOnly(new[]
    {
        new ShopItem("rice", "暖心饭团", "饱食 +25", 12, Food:25),
        new ShopItem("water", "清泉水", "水分 +30", 8, Water:30),
        new ShopItem("fruit", "鲜果拼盘", "饱食 +12 · 水分 +12 · 心情 +8", 16, Food:12, Water:12, Mood:8),
        new ShopItem("cake", "莓莓蛋糕", "饱食 +15 · 心情 +20", 22, Food:15, Mood:20),
        new ShopItem("soup", "营养汤", "饱食 +10 · 水分 +10 · 健康 +20", 24, Food:10, Water:10, Health:20),
        new ShopItem("plum", "暮紫企鹅", "默认企鹅配色 · 永久外观", 100, Skin:"plum"),
        new ShopItem("mint", "薄荷企鹅", "默认企鹅配色 · 永久外观", 100, Skin:"mint")
    });
    public static readonly IReadOnlyList<PetActivity> Activities = Array.AsReadOnly(new[]
    {
        new PetActivity("work", "打工", 5, 15, "梦币 +30"),
        new PetActivity("study", "学习", 3, 12, "技能点 +1（可兑换 15 XP）"),
        new PetActivity("exercise", "运动", 4, 18, "体能 +1 · 健康上限 +1（最高 150） · 健康 +8"),
        new PetActivity("play", "玩耍", 2, 10, "心情 +18"),
        new PetActivity("sleep", "睡觉", 5, 0, "精力 +40 · 健康 +5")
    });

    public static void Normalize(PetState p, DateTimeOffset now)
    {
        p.Coins = Math.Clamp(p.Coins, 0, 999999); p.Hydration = Math.Clamp(p.Hydration, 0, 100);
        p.MaxHealth = Math.Clamp(p.MaxHealth, 100, 150); p.Health = Math.Clamp(p.Health, 0, p.MaxHealth);
        p.SkillPoints = Math.Clamp(p.SkillPoints, 0, 9999); p.Fitness = Math.Clamp(p.Fitness, 0, 100);
        p.Inventory = (p.Inventory ?? new()).Where(x => Items.Any(i => i.Id == x.Key && i.Skin == "") && x.Value > 0)
            .ToDictionary(x => x.Key, x => Math.Clamp(x.Value, 0, 999));
        p.OwnedSkins = (p.OwnedSkins ?? []).Where(x => x is "blue" or "plum" or "mint").Append("blue").Distinct().ToList();
        if (!p.OwnedSkins.Contains(p.Skin)) p.Skin = "blue";
        var a = Activities.FirstOrDefault(x => x.Id == p.Activity);
        if (a is null || p.ActivityStartedUtc is null || p.ActivityEndsUtc is null
            || p.ActivityEndsUtc < p.ActivityStartedUtc || p.ActivityEndsUtc - p.ActivityStartedUtc > TimeSpan.FromMinutes(a.Minutes + 1)
            || p.ActivityStartedUtc > now.AddMinutes(1)) ClearActivity(p);
        if (p.LastCareUtc > now || p.LastCareUtc < now.AddDays(-7)) p.LastCareUtc = now;
        p.FoodDecayFraction = Fraction(p.FoodDecayFraction); p.WaterDecayFraction = Fraction(p.WaterDecayFraction); p.EnergyDecayFraction = Fraction(p.EnergyDecayFraction);
        p.CareVersion = 1;
    }

    public static bool Advance(PetState p, DateTimeOffset now)
    {
        var changed = false;
        // Only charge up to one hour after absence; pets never die or lose levels.
        var steps = (int)Math.Clamp((now - p.LastCareUtc).TotalMinutes / 10, 0, 6);
        if (steps > 0)
        {
            var resistance = 1 - Math.Min(0.4, p.Fitness * 0.004);
            var food = steps * 2 * resistance + p.FoodDecayFraction;
            var water = steps * 3 * resistance + p.WaterDecayFraction;
            var energy = steps * resistance + p.EnergyDecayFraction;
            p.Fullness = Math.Max(0, p.Fullness - (int)food); p.FoodDecayFraction = food % 1;
            p.Hydration = Math.Max(0, p.Hydration - (int)water); p.WaterDecayFraction = water % 1;
            p.Energy = Math.Max(0, p.Energy - (int)energy); p.EnergyDecayFraction = energy % 1;
            p.Mood = Math.Max(0, p.Mood - steps);
            if (p.Fullness < 15 || p.Hydration < 15) p.Health = Math.Max(0, p.Health - steps);
            p.LastCareUtc = (now - p.LastCareUtc).TotalMinutes > 60 ? now : p.LastCareUtc.AddMinutes(steps * 10);
            changed = true;
        }
        if (p.LastCareUtc > now) { p.LastCareUtc = now; changed = true; }
        if (p.Activity != "" && p.ActivityEndsUtc is { } end && now >= end)
        {
            switch (p.Activity)
            {
                case "work": p.Coins = Math.Min(999999, p.Coins + 30); break;
                case "study": p.SkillPoints = Math.Min(9999, p.SkillPoints + 1); break;
                case "exercise": p.Fitness = Math.Min(100, p.Fitness + 1); p.MaxHealth = Math.Min(150, p.MaxHealth + 1); p.Health = Math.Min(p.MaxHealth, p.Health + 8); break;
                case "play": p.Mood = Math.Min(100, p.Mood + 18); break;
                case "sleep": p.Energy = Math.Min(100, p.Energy + 40); p.Health = Math.Min(p.MaxHealth, p.Health + 5); break;
            }
            ClearActivity(p); changed = true;
        }
        return changed;
    }

    public static string Buy(PetState p, string id)
    {
        var item = Items.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("商品不存在。");
        if (item.Skin != "" && p.OwnedSkins.Contains(item.Skin)) throw new InvalidOperationException("已拥有此外观，无需重复购买。");
        if (p.Coins < item.Price) throw new InvalidOperationException("梦币不足，可以先去打工。");
        if (item.Skin == "" && p.Inventory.GetValueOrDefault(id) >= 999) throw new InvalidOperationException("背包中该商品已满。");
        p.Coins -= item.Price;
        if (item.Skin != "") p.OwnedSkins.Add(item.Skin);
        else p.Inventory[id] = p.Inventory.GetValueOrDefault(id) + 1;
        return "已购买" + item.Name + "，已放入背包。";
    }

    public static string Consume(PetState p, string id)
    {
        var item = Items.FirstOrDefault(x => x.Id == id && x.Skin == "") ?? throw new InvalidOperationException("该物品不能投喂。");
        if (p.Inventory.GetValueOrDefault(id) < 1) throw new InvalidOperationException("背包里没有该物品，请先在商店购买。");
        if (p.Activity == "sleep") throw new InvalidOperationException("浅梦正在睡觉，醒来再投喂吧。");
        if (!(item.Food > 0 && p.Fullness < 100 || item.Water > 0 && p.Hydration < 100 || item.Mood > 0 && p.Mood < 100 || item.Health > 0 && p.Health < p.MaxHealth))
            throw new InvalidOperationException("对应属性已满，先留在背包里吧。");
        p.Inventory[id]--; if (p.Inventory[id] == 0) p.Inventory.Remove(id);
        p.Fullness = Math.Min(100, p.Fullness + item.Food); p.Hydration = Math.Min(100, p.Hydration + item.Water);
        p.Mood = Math.Min(100, p.Mood + item.Mood); p.Health = Math.Min(p.MaxHealth, p.Health + item.Health);
        p.AddAffection(1); return "已使用" + item.Name + "。";
    }

    public static string Start(PetState p, string id, DateTimeOffset now)
    {
        if (p.Activity != "") throw new InvalidOperationException("已有进行中的活动，请等待完成或先结束。");
        var a = Activities.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("活动不存在。");
        if (id == "sleep" && p.Energy >= 100) throw new InvalidOperationException("精力已满，暂时不需要睡觉。");
        if (p.Energy < a.Energy) throw new InvalidOperationException("精力不足，先睡一觉恢复吧。");
        if (id != "sleep" && id != "work" && (p.Fullness < 10 || p.Hydration < 10 || p.Health < 10)) throw new InvalidOperationException("状态太低，请先补充食物、水分或营养汤。");
        p.Energy -= a.Energy; p.Activity = id; p.ActivityStartedUtc = now; p.ActivityEndsUtc = now.AddMinutes(a.Minutes);
        return $"开始{a.Name}，{a.Minutes} 分钟后完成。";
    }
    public static string Cancel(PetState p)
    {
        if (p.Activity == "") throw new InvalidOperationException("当前没有活动。");
        ClearActivity(p); return "活动已结束。提前结束不领取奖励，已消耗的精力不退还。";
    }
    public static string SpendSkill(PetState p)
    {
        if (p.SkillPoints < 1) throw new InvalidOperationException("技能点不足，学习完成后可获得技能点。");
        p.SkillPoints--; p.Xp += 15;
        while (p.Xp >= p.XpToNextLevel && p.Level < 9999) { p.Xp -= p.XpToNextLevel; p.Level++; }
        p.Xp = Math.Min(p.Xp, p.XpToNextLevel - 1);
        return "已消耗 1 技能点，获得 15 XP。";
    }
    private static double Fraction(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 0.999999) : 0;
    private static void ClearActivity(PetState p) { p.Activity = ""; p.ActivityStartedUtc = null; p.ActivityEndsUtc = null; }
}
