using ARIS.API.Services;
using ARIS.Shared.Entities;
using Microsoft.AspNetCore.Mvc;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DictionaryController : ControllerBase
{
    private readonly DictionaryService _service;

    public DictionaryController(DictionaryService service)
    {
        _service = service;
    }

    [HttpPost("search/roles")]
    public async Task<ActionResult<List<RefRole>>> SearchRoles([FromBody] SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query cannot be empty.");

        var results = await _service.SearchRolesAsync(request.Query);
        return Ok(results);
    }

    [HttpPost("search/skills")]
    public async Task<ActionResult<List<RefSkill>>> SearchSkills([FromBody] SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Query cannot be empty.");

        var results = await _service.SearchSkillsAsync(request.Query);
        return Ok(results);
    }

    [HttpPost("recommend/jobs")]
    public async Task<ActionResult<string>> RecommendJobs([FromBody] SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Prompt cannot be empty.");

        var result = await _service.GetJobRecommendationsAsync(request.Query);
        return Ok(result);
    }

    [HttpPost("recommend/skills")]
    public async Task<ActionResult<string>> RecommendSkills([FromBody] SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest("Prompt cannot be empty.");

        var result = await _service.GetSkillRecommendationsAsync(request.Query);
        return Ok(result);
    }
}

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
}
