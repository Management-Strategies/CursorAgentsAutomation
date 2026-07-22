using System.Text.Json;
using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

public class last_standardize_form_state_store
{
    private static readonly JsonSerializerOptions json_options = new()
    {
        WriteIndented = true
    };

    private readonly string _dir;
    private readonly string _state_path;

    public last_standardize_form_state_store(IWebHostEnvironment env)
    {
        _dir = Path.Combine(env.ContentRootPath, "wwwroot", "standardized", "_last");
        Directory.CreateDirectory(_dir);
        _state_path = Path.Combine(_dir, ".last_standardize_form_state.json");
    }

    public last_standardize_form_state? load()
    {
        if (!File.Exists(_state_path))
            return null;

        try
        {
            var json = File.ReadAllText(_state_path);
            return JsonSerializer.Deserialize<last_standardize_form_state>(json);
        }
        catch
        {
            return null;
        }
    }

    public void save(last_standardize_form_state state)
    {
        Directory.CreateDirectory(_dir);
        var json = JsonSerializer.Serialize(state, json_options);
        File.WriteAllText(_state_path, json);
    }

    public string? resolve_saved_input_path(last_standardize_form_state state)
    {
        if (string.IsNullOrWhiteSpace(state.saved_input_file_name))
            return null;

        var path = Path.Combine(_dir, state.saved_input_file_name);
        return File.Exists(path) ? path : null;
    }

    public string save_persistent_upload(IFormFile file)
    {
        Directory.CreateDirectory(_dir);
        var safe = Path.GetFileNameWithoutExtension(file.FileName);
        foreach (var c in Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(safe))
            safe = "upload";

        var name = $"{safe}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.xlsx";
        var path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        file.CopyTo(stream);
        return path;
    }

    public string copy_to_persistent(string source_path, string? original_file_name)
    {
        Directory.CreateDirectory(_dir);
        var base_name = Path.GetFileNameWithoutExtension(
            string.IsNullOrWhiteSpace(original_file_name)
                ? Path.GetFileName(source_path)
                : original_file_name);
        foreach (var c in Path.GetInvalidFileNameChars())
            base_name = base_name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(base_name))
            base_name = "upload";

        var name = $"{base_name}_{DateTime.UtcNow:yyyyMMddHHmmssfff}.xlsx";
        var dest = Path.Combine(_dir, name);
        File.Copy(source_path, dest, overwrite: true);
        return dest;
    }
}
