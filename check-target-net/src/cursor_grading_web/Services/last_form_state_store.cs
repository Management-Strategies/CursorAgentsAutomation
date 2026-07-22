using System.Text.Json;
using cursor_grading_web.Models;

namespace cursor_grading_web.Services;

public class last_form_state_store
{
    private static readonly JsonSerializerOptions json_options = new()
    {
        WriteIndented = true
    };

    private readonly string _state_path;

    public last_form_state_store(IWebHostEnvironment env)
    {
        var uploads_dir = Path.Combine(env.ContentRootPath, "wwwroot", "uploads");
        Directory.CreateDirectory(uploads_dir);
        _state_path = Path.Combine(uploads_dir, ".last_form_state.json");
    }

    public last_form_state? load()
    {
        if (!File.Exists(_state_path))
            return null;

        try
        {
            var json = File.ReadAllText(_state_path);
            return JsonSerializer.Deserialize<last_form_state>(json);
        }
        catch
        {
            return null;
        }
    }

    public void save(last_form_state state)
    {
        var json = JsonSerializer.Serialize(state, json_options);
        File.WriteAllText(_state_path, json);
    }

    public string? resolve_saved_input_path(last_form_state state)
    {
        if (string.IsNullOrWhiteSpace(state.saved_input_file_name))
            return null;

        var path = Path.Combine(Path.GetDirectoryName(_state_path)!, state.saved_input_file_name);
        return File.Exists(path) ? path : null;
    }
}
