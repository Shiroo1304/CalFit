using System.Text.Json;
using CalFit.Models;
using Microsoft.Maui.Storage;
namespace CalFit.Services;
/// <summary>Ingredient and personal notes are local-only: current API has no ingredients fields.</summary>
public sealed class MealNotesService
{
    private static string Key(int userId, int mealId) => $"calfit.mealnotes.{userId}.{mealId}";
    public MealNotes Get(int userId, int mealId)
    {
        var json = Preferences.Default.Get(Key(userId, mealId), "");
        try { return JsonSerializer.Deserialize<MealNotes>(json) ?? new(); }
        catch { return new(); }
    }
    public void Save(int userId, int mealId, MealNotes notes)
        => Preferences.Default.Set(Key(userId, mealId), JsonSerializer.Serialize(notes));
    public void Delete(int userId, int mealId) => Preferences.Default.Remove(Key(userId, mealId));
}
