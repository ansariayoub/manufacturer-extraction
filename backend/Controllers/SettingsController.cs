using Microsoft.AspNetCore.Mvc;
using ManufacturerExtraction.Api.Dtos;
using ManufacturerExtraction.Api.Services;
using ManufacturerExtraction.Api.Services.Auth;

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
    private readonly EmbeddingModelSettingsService _embeddingModel;
    private readonly ExtractionPromptSettingsService _prompt;
    private readonly CurrentUserAccessor _currentUser;

    public SettingsController(
        AiModelSettingsService aiModel, EmbeddingModelSettingsService embeddingModel,
        ExtractionPromptSettingsService prompt, CurrentUserAccessor currentUser)
    {
        _prompt = prompt;
        _aiModel = aiModel;
        _embeddingModel = embeddingModel;
        _currentUser = currentUser;
    }

    // The model deployments are shared by every client (one Azure OpenAI resource, one bill), so
    // changing them is an admin-only action; anyone signed in may read the current choice.
    private bool IsAdmin => _currentUser.Current.IsAdmin;

    [HttpGet("ai-model")]
    public ActionResult<AiModelSettingsDto> GetAiModel() =>
        Ok(new AiModelSettingsDto(_aiModel.CurrentDeployment, _aiModel.AvailableDeployments));

    [HttpPut("ai-model")]
    public async Task<ActionResult<AiModelSettingsDto>> SetAiModel(UpdateAiModelRequest request, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        var deployment = request.Deployment?.Trim();
        if (string.IsNullOrWhiteSpace(deployment))
            return BadRequest("Deployment name is required.");

        if (!_aiModel.AvailableDeployments.Contains(deployment, StringComparer.OrdinalIgnoreCase))
            return BadRequest($"'{deployment}' is not one of the available deployments: {string.Join(", ", _aiModel.AvailableDeployments)}.");

        await _aiModel.SetDeploymentAsync(deployment, ct);
        return Ok(new AiModelSettingsDto(_aiModel.CurrentDeployment, _aiModel.AvailableDeployments));
    }

    private ExtractionPromptDto PromptDto() => new(
        _prompt.CurrentPrompt, _prompt.DefaultPrompt, _prompt.IsCustomized,
        AnalyticsTransformationService.SchemaJson, ExtractionPromptSettingsService.MaxLength);

    // The prompt drives every client's extractions and can degrade them all, so unlike the model
    // choice it is admin-only to read as well as to change.
    [HttpGet("extraction-prompt")]
    public ActionResult<ExtractionPromptDto> GetExtractionPrompt() =>
        IsAdmin ? Ok(PromptDto()) : Forbid();

    [HttpPut("extraction-prompt")]
    public async Task<ActionResult<ExtractionPromptDto>> SetExtractionPrompt(UpdateExtractionPromptRequest request, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        var text = request.Prompt ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return BadRequest("The prompt cannot be empty.");
        if (text.Length > ExtractionPromptSettingsService.MaxLength)
            return BadRequest($"The prompt is limited to {ExtractionPromptSettingsService.MaxLength:N0} characters.");

        await _prompt.SetPromptAsync(text, ct);
        return Ok(PromptDto());
    }

    [HttpDelete("extraction-prompt")]
    public async Task<ActionResult<ExtractionPromptDto>> ResetExtractionPrompt(CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        await _prompt.ResetAsync(ct);
        return Ok(PromptDto());
    }

    [HttpGet("embedding-model")]
    public ActionResult<EmbeddingModelSettingsDto> GetEmbeddingModel() =>
        Ok(new EmbeddingModelSettingsDto(_embeddingModel.CurrentDeployment, _embeddingModel.AvailableDeployments));

    [HttpPut("embedding-model")]
    public async Task<ActionResult<EmbeddingModelSettingsDto>> SetEmbeddingModel(UpdateEmbeddingModelRequest request, CancellationToken ct)
    {
        if (!IsAdmin) return Forbid();

        var deployment = request.Deployment?.Trim();
        if (string.IsNullOrWhiteSpace(deployment))
            return BadRequest("Deployment name is required.");

        if (!_embeddingModel.AvailableDeployments.Contains(deployment, StringComparer.OrdinalIgnoreCase))
            return BadRequest($"'{deployment}' is not one of the available deployments: {string.Join(", ", _embeddingModel.AvailableDeployments)}.");

        await _embeddingModel.SetDeploymentAsync(deployment, ct);
        return Ok(new EmbeddingModelSettingsDto(_embeddingModel.CurrentDeployment, _embeddingModel.AvailableDeployments));
    }
}
