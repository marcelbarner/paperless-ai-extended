using Microsoft.AspNetCore.Mvc;
using PaperlessAI.API.Services;

namespace PaperlessAI.API.Controllers;

[ApiController]
[Route("api/chat")]
public class ChatController(ChatService chat) : ControllerBase
{
    [HttpPost("message")]
    public async Task<IActionResult> Message([FromBody] ChatRequest request, CancellationToken ct)
    {
        var response = await chat.SendMessageAsync(request.History, request.Message, ct);
        return Ok(response);
    }
}
