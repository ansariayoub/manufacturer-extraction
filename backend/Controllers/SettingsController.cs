using Microsoft.AspNetCore.Mvc;
using ManufacturerExtraction.Api.Dtos;
using ManufacturerExtraction.Api.Services;

namespace ManufacturerExtraction.Api.Controllers;

/// <summary>
/// App-wide settings that aren't tied to a specific manufacturer — first (and so far only) one is
/// which Azure OpenAI deployment the canonical-mapping pipeline calls. See AiModelSettingsService.
/// </summary>
[ApiController]
[Route("api/settings")]
public class SettingsController : ControllerBase
{
    private readonly AiModelSettingsService _aiModel;

    public SettingsController(AiModelSettingsService aiModel) => _aiModel = aiModel;

    [HttpGet("ai-model")]
    public ActionResult<AiModelSettingsDto> GetAiModel() =>
        Ok(new AiModelSettingsDto(_aiModel.CurrentDeployment, _aiModel.AvailableDeployments));

    [HttpPut("ai-model")]
    public async Task<ActionResult<AiModelSettingsDto>> SetAiModel(UpdateAiModelRequest request, CancellationToken ct)
    {
        var deployment = request.Deployment?.Trim();
        if (string.IsNullOrWhiteSpace(deployment))
            return BadRequest("Deployment name is required.");

        if (!_aiModel.AvailableDeployments.Contains(deployment, StringComparer.OrdinalIgnoreCase))
            return BadRequest($"'{deployment}' is not one of the available deployments: {string.Join(", ", _aiModel.AvailableDeployments)}.");

        await _aiModel.SetDeploymentAsync(deployment, ct);
        return Ok(new AiModelSettingsDto(_aiModel.CurrentDeployment, _aiModel.AvailableDeployments));
    }
}
