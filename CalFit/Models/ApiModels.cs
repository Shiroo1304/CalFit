using System.Text.Json.Serialization;
namespace CalFit.Models;

public sealed class DashboardSummary
{
    public int CalorieGoal { get; set; }
    public int CaloriesConsumed { get; set; }
    public int CaloriesRemaining { get; set; }
    public double ProteinGrams { get; set; }
    public double CarbsGrams { get; set; }
    public double FatGrams { get; set; }
    public int MealCount { get; set; }
}

public sealed class UserProfile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public int CalorieGoal { get; set; } = 2000;
    public double ProteinGoal { get; set; } = 120;
    public double CarbsGoal { get; set; } = 250;
    public double FatGoal { get; set; } = 65;
    public double? HeightCm { get; set; }
    public double? WeightKg { get; set; }
}

public sealed class Meal
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string FoodName { get; set; } = "";
    public string MealType { get; set; } = "Lunch";
    public double WeightGrams { get; set; }
    public int Calories { get; set; }
    public double ProteinGrams { get; set; }
    public double CarbsGrams { get; set; }
    public double FatGrams { get; set; }
    public string? ImageUrl { get; set; }
    public double? AiConfidence { get; set; }
    [JsonPropertyName("createAT")]
    public DateTime CreateAT { get; set; }
}

public sealed class FoodAnalysisResult
{
    public bool IsFood { get; set; }
    public string FoodName { get; set; } = "";
    public double WeightGrams { get; set; }
    public int Calories { get; set; }
    public double ProteinGrams { get; set; }
    public double CarbsGrams { get; set; }
    public double FatGrams { get; set; }
    public string Confidence { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class MealNotes
{
    public string Ingredients { get; set; } = "";
    public string Note { get; set; } = "";
}
