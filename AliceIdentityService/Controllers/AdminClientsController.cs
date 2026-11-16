using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AliceIdentityService.Controllers;

[ApiController, Route("api/admin/clients"), Authorize(Policy = AisConstants.AdminPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(48 * 1024)]
public sealed class AdminClientsController(AdminClientService clients) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminClientPage>> Search([FromQuery] AdminClientQuery query, CancellationToken cancellationToken) =>
        await clients.SearchAsync(query, cancellationToken);

    [HttpGet("{id}")]
    public async Task<ActionResult<AdminClientDetail>> Detail(string id, CancellationToken cancellationToken)
    {
        var client = await clients.FindAsync(id, cancellationToken);
        return client is null ? NotFound() : client;
    }

    [HttpPost]
    public async Task<IActionResult> Create(AdminCreateClientInput input, CancellationToken cancellationToken) =>
        Result(await clients.CreateAsync(User, input, cancellationToken));

    [HttpPost("{id}")]
    public async Task<IActionResult> Edit(string id, AdminEditClientInput input, CancellationToken cancellationToken) =>
        Result(await clients.EditAsync(User, id, input, cancellationToken));

    [HttpPost("{id}/credentials")]
    public async Task<IActionResult> Credentials(string id, AdminClientCredentialsInput input, CancellationToken cancellationToken) =>
        Result(await clients.CredentialsAsync(User, id, input, cancellationToken));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, AdminDeleteUserInput input, CancellationToken cancellationToken) =>
        Result(await clients.DeleteAsync(User, id, input, cancellationToken));

    private IActionResult Result(AdminMutationOutcome outcome) => outcome.Status switch
    {
        200 => Ok(new AdminMutationResult(outcome.Id!, false)),
        401 => Unauthorized(),
        403 => Forbid(),
        _ => Problem(statusCode: outcome.Status, title: outcome.Error)
    };
}
