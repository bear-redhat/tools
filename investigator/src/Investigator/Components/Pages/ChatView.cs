using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Investigator.Components.Pages;

[Route("/c/{ConversationId}/view")]
[AllowAnonymous]
public class ChatView : Chat
{
    protected override bool IsReadonly => true;
}
