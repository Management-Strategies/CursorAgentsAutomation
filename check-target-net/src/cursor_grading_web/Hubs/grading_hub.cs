using Microsoft.AspNetCore.SignalR;

namespace cursor_grading_web.Hubs;

public class grading_hub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
    }
}