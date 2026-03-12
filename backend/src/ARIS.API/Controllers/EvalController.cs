using ARIS.API.Services;
using ARIS.Shared.Data;
using ARIS.Shared.Entities;
using ARIS.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ARIS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EvalController : ControllerBase
{
    private readonly MatchService _matchService;
    private readonly GroundingService _groundingService;
    private readonly ExtractionBenchmarkService _benchmarkService;
    private readonly ArisDbContext _context;
    private readonly IChatClient _chatClient;
    private readonly ILogger<EvalController> _logger;
    private readonly ResumeService _resumeService;
    private readonly ResumePdfService _resumePdfService;
    private readonly GraphService _graphService;

    public EvalController(
        MatchService matchService,
        GroundingService groundingService,
        ExtractionBenchmarkService benchmarkService,
        ArisDbContext context,
        IChatClient chatClient,
        ILogger<EvalController> logger,
        ResumeService resumeService,
        ResumePdfService resumePdfService,
        GraphService graphService)
    {
        _matchService = matchService;
        _groundingService = groundingService;
        _benchmarkService = benchmarkService;
        _context = context;
        _chatClient = chatClient;
        _logger = logger;
        _resumeService = resumeService;
        _resumePdfService = resumePdfService;
        _graphService = graphService;
    }

    public class GroundingRequest
    {
        public string Text { get; set; } = "";
        public Guid UserProfileId { get; set; }
    }

    [HttpPost("grounding")]
    public async Task<IActionResult> ComputeGroundingScore([FromBody] GroundingRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.UserProfileId == Guid.Empty)
            return BadRequest("Text and UserProfileId are required.");

        var user = await _context.UserProfiles.FindAsync(request.UserProfileId);
        if (user?.CleanSignal == null)
            return NotFound($"User profile {request.UserProfileId} not found or has no CleanSignal.");

        var userSkills = user.CleanSignal.Skills.Select(s => s.Name).ToList();
        var result = await _groundingService.CalculateGroundingScoreAsync(request.Text, userSkills);
        return Ok(result);
    }

    [HttpPost("extraction-benchmark")]
    public async Task<IActionResult> ExtractionBenchmark([FromBody] ExtractionBenchmarkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest("Text is required.");
        if (request.Runs < 1 || request.Runs > 10)
            return BadRequest("Runs must be between 1 and 10.");
        if (request.Models == null || request.Models.Count == 0)
            return BadRequest("At least one model must be specified.");

        var result = await _benchmarkService.RunBenchmarkAsync(request);
        return Ok(result);
    }

    public class TailorDeltaRequest
    {
        public Guid ResumeId { get; set; }
        public Guid JobId { get; set; }
    }

    [HttpPost("tailor-delta")]
    public async Task<IActionResult> TailorDelta([FromBody] TailorDeltaRequest request)
    {
        if (request.ResumeId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("ResumeId and JobId are required.");

        var sw = Stopwatch.StartNew();

        var baselineMatch = await _matchService.AnalyzeMatchAsync(request.ResumeId, request.JobId);
        if (baselineMatch == null)
            return NotFound("Match analysis failed. Ensure both IDs are valid and fully processed.");

        var job = await _context.JobPostings.FindAsync(request.JobId);
        if (job?.CleanSignal == null)
            return NotFound($"Job {request.JobId} not found or has no CleanSignal.");

        var user = await _context.UserProfiles.FindAsync(request.ResumeId);

        string rawResumeText;
        try
        {
            using var doc = JsonDocument.Parse(user?.RawResume ?? "{}");
            rawResumeText = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : user?.RawResume ?? "";
        }
        catch { rawResumeText = user?.RawResume ?? ""; }
        var rawJobText = job.RawDescription ?? "";

        var tailoredData = await _resumeService.BuildTailoredResumeDataAsync(
            request.ResumeId, request.JobId, precomputedMatch: baselineMatch);
        if (tailoredData == null)
            return StatusCode(500, "Tailoring pipeline failed.");

        var pdfBytes = _resumePdfService.GeneratePdf(
            tailoredData.PersonalInfo,
            tailoredData.CleanSignal,
            tailoredData.ProfessionalSummary,
            tailoredData.TailoredBullets);

        var originalMappings = BuildOriginalMappings(tailoredData.CleanSignal, job.CleanSignal);
        var canonicalSkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(
            tailoredData.TailoredBullets, tailoredData.ProfessionalSummary ?? "", originalMappings);
        var scoring = _matchService.VerifiedMatchScore(baselineMatch, canonicalSkills, job.CleanSignal);

        var summarySemanticScore = await ComputeSemanticSimilarityAsync(tailoredData.ProfessionalSummary ?? "", rawJobText);
        var summarySemanticBaseline = await ComputeSemanticSimilarityAsync(rawResumeText, rawJobText);
        var summaryBridgeRate = await ComputeSummaryBridgeRate(tailoredData.ProfessionalSummary ?? "", baselineMatch, tailoredData.CleanSignal, job.CleanSignal);

        sw.Stop();

        return Ok(new
        {
            resumeId                    = request.ResumeId,
            jobId                       = request.JobId,
            latencyMs                   = sw.ElapsedMilliseconds,
            summaryBridgeRate           = summaryBridgeRate,
            summarySemanticScore        = summarySemanticScore,
            summarySemanticBaseline     = summarySemanticBaseline,
            summarySemanticDeltaPercent = summarySemanticBaseline > 0
                ? Math.Round((summarySemanticScore - summarySemanticBaseline) / summarySemanticBaseline * 100.0, 2)
                : 0.0,
            hallucinationCount          = scoring.HallucinationCount,
            hallucinations              = scoring.Hallucinations,
            baselineGraphScore          = scoring.BaselineScore,
            tailoredGraphScore          = scoring.VerifiedScore,
            graphScoreDeltaPercent      = Math.Round(scoring.Delta * 100.0, 2),
            summaryText                 = tailoredData.ProfessionalSummary,
            pdfBase64                   = Convert.ToBase64String(pdfBytes)
        });
    }

    public class TailorCompareRequest
    {
        public Guid ResumeId { get; set; }
        public Guid JobId { get; set; }
    }

    private record TailorPipelineResult(
        double SummaryBridgeRate,
        double SummarySemanticScore,
        double SummarySemanticBaseline,
        int HallucinationCount,
        long LatencyMs,
        byte[] PdfBytes,
        string SummaryText,
        double BaselineGraphScore,
        double TailoredGraphScore,
        double GraphScoreDelta
    );

    // RAGAS Contexts Endpoint
    [HttpPost("ragas-contexts")]
    public async Task<IActionResult> RagasContexts([FromBody] RagasContextRequest request)
    {
        if (request.ResumeId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("ResumeId and JobId are required.");

        var user = await _context.UserProfiles.FindAsync(request.ResumeId);
        var job  = await _context.JobPostings.FindAsync(request.JobId);

        if (user?.CleanSignal == null)
            return NotFound($"User profile {request.ResumeId} not found or has no CleanSignal.");
        if (job?.CleanSignal == null)
            return NotFound($"Job {request.JobId} not found or has no CleanSignal.");

        // Raw text helpers+
        string rawResumeText;
        try
        {
            using var doc = JsonDocument.Parse(user.RawResume ?? "{}");
            rawResumeText = doc.RootElement.TryGetProperty("content", out var c)
                ? c.GetString() ?? "" : user.RawResume ?? "";
        }
        catch { rawResumeText = user.RawResume ?? ""; }
        var rawJobText = job.RawDescription ?? "";

        var jobTitle       = job.CleanSignal.TargetRoles?.FirstOrDefault()?.Title ?? "the role";
        var candidateTitle = user.CleanSignal.Roles?.FirstOrDefault()?.Title ?? "the candidate";

        var candidateSkillNames = user.CleanSignal.Skills.Select(s => s.Name).ToList();
        var jobSkillNames       = job.CleanSignal.RequiredSkills.Select(s => s.Name).ToList();

        //  GraphRAG pipeline
        var matchResult = await _matchService.AnalyzeMatchAsync(request.ResumeId, request.JobId);
        if (matchResult == null)
            return NotFound("Match analysis failed. Ensure both IDs are valid and fully processed.");

        var graphRagContexts = BuildGraphRagContexts(matchResult, job.CleanSignal);

        // Generate GraphRAG answer using the grounded summary path
        var (graphRagAnswer, graphRagGroundingScore) =
            await _matchService.GenerateGroundedSummaryAsync(request.ResumeId, request.JobId);

        var tierCounts = new
        {
            t1 = matchResult.MatchingSkills.Count,
            t2 = matchResult.ImplicitlyDiscoveredSkills.Count,
            t3 = matchResult.PrerequisiteMetSkills.Count,
            t4 = matchResult.BridgeableSkills.Count,
            t5 = matchResult.HardGaps.Count,
        };

        // Vector-RAG pipeline 
        // Build vector contexts: top-15 job skills ranked by cosine similarity to resume embedding.
        // We reuse the job's required skill list with the vector similarity score for the whole pair
        // as proxy (no per-skill embedding query needed — skills are already grounded canonical names).
        // For a richer per-skill similarity, we compare each job skill name against the candidate
        // skill text using the resume's stored vector vs. a freshly-generated skill-name embedding.
        var vectorRagContexts = await BuildVectorRagContextsAsync(
            user, job, candidateSkillNames, jobSkillNames, topK: 15);

        // Generate Vector-RAG answer: same approach as Pipeline A in tailor-compare.
        // Raw text + top skill names, no graph context.
        var refSkillContext = string.Join(", ", jobSkillNames.Take(20));
        var vectorRagPrompt = $$"""
            You are a career advisor. Assess how well the candidate matches the job based solely on the information provided.
            Write a concise 2-3 paragraph professional assessment in third person.
            Do not reference any information not present in the resume or job description below.

            CANDIDATE RESUME:
            {{rawResumeText}}

            JOB DESCRIPTION:
            {{rawJobText}}

            RELEVANT SKILLS FOR THIS ROLE:
            {{refSkillContext}}

            Provide a balanced assessment covering strengths and any notable gaps.
            """;

        string vectorRagAnswer;
        try
        {
            var resp = await _chatClient.GetResponseAsync(vectorRagPrompt);
            vectorRagAnswer = resp?.Text?.Trim() ?? "Answer generation failed.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vector-RAG answer generation failed.");
            vectorRagAnswer = "Answer generation failed.";
        }

        return Ok(new
        {
            question = $"How qualified is this candidate for {jobTitle}?",
            graphRag = new
            {
                contexts        = graphRagContexts,
                answer          = graphRagAnswer,
                groundingScore  = graphRagGroundingScore,
                tierCounts,
            },
            vectorRag = new
            {
                contexts = vectorRagContexts,
                answer   = vectorRagAnswer,
            },
            candidateSkills = candidateSkillNames,
            jobSkills       = jobSkillNames,
        });
    }

    /// <summary>
    /// Formats the GraphRAG match tiers into plain-English context strings for RAGAS evaluation.
    /// Each string describes one skill relationship provable from the ARIS knowledge graph or
    /// from direct match evidence.
    /// </summary>
    private static List<string> BuildGraphRagContexts(
        MatchAnalysisResult match,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal jobSignal)
    {
        var contexts = new List<string>();

        // Tier 1 — direct matches
        foreach (var s in match.MatchingSkills)
        {
            var yearsClause = s.YearsRequired > 0
                ? $"candidate: {s.CandidateYears:0.#}y, required: {s.YearsRequired:0.#}y"
                : $"candidate: {s.CandidateYears:0.#}y";
            contexts.Add($"DIRECT MATCH: {s.SkillName} ({yearsClause}, {s.Importance})");
        }

        // Tier 2 — implicitly discovered via SUBSET_OF traversal
        foreach (var s in match.ImplicitlyDiscoveredSkills)
            contexts.Add($"FOUNDATION: {s} covered via SUBSET_OF graph traversal");

        // Tier 3 — prerequisite met (candidate has parent/foundation skill)
        foreach (var s in match.PrerequisiteMetSkills)
        {
            var bridgeNote = !string.IsNullOrWhiteSpace(s.BridgePath)
                ? $" ({s.BridgePath})" : "";
            var src = !string.IsNullOrWhiteSpace(s.BridgeSource) ? $", source={s.BridgeSource}" : "";
            contexts.Add($"TRANSFERABLE: {s.SkillName}{bridgeNote}{src}");
        }

        // Tier 4 — bridgeable via IS_SIMILAR_TO or graph bridge
        foreach (var s in match.BridgeableSkills)
        {
            var bridgeNote = !string.IsNullOrWhiteSpace(s.BridgePath)
                ? $" {s.BridgePath}" : "";
            var src = !string.IsNullOrWhiteSpace(s.BridgeSource) ? $", source={s.BridgeSource}" : "";
            contexts.Add($"ADJACENT: {s.SkillName}{bridgeNote}{src}");
        }

        // Tier 5 — hard gaps (no graph path; informational)
        foreach (var s in match.HardGaps)
            contexts.Add($"HARD GAP: {s.SkillName} — no graph-provable path from candidate skills");

        return contexts;
    }

    /// <summary>
    /// Builds Vector-RAG context strings by computing cosine similarity between the resume
    /// embedding and per-skill embeddings generated on demand, then ranking job skills by score.
    /// Returns the top-K job skills with their similarity scores as plain-English context strings.
    /// Falls back to returning the job skill list without per-skill scores if embedding generation fails.
    /// </summary>
    private async Task<List<string>> BuildVectorRagContextsAsync(
        ARIS.Shared.Entities.UserProfile user,
        ARIS.Shared.Entities.JobPosting job,
        List<string> candidateSkillNames,
        List<string> jobSkillNames,
        int topK = 15)
    {
        if (user.Embedding == null || jobSkillNames.Count == 0)
            return jobSkillNames.Take(topK).Select(s => $"{s} (vector match)").ToList();

        var resumeVec = user.Embedding.ToArray();

        var scored = new List<(string SkillName, double Similarity)>();

        // For each job skill, check if the candidate also has it (direct overlap gets 1.0 proxy).
        // For non-matching skills, we compare the resume embedding to each skill-name embedding.
        var candidateSet = new HashSet<string>(candidateSkillNames, StringComparer.OrdinalIgnoreCase);

        foreach (var skillName in jobSkillNames)
        {
            if (candidateSet.Contains(skillName))
            {
                // Exact match — highest possible similarity
                scored.Add((skillName, 1.0));
                continue;
            }

            // Generate embedding for skill name and compute cosine distance to resume
            try
            {
                var skillEmb = await _resumeService.GenerateEmbeddingAsync(skillName);
                double dot = 0, normR = 0, normS = 0;
                for (int i = 0; i < resumeVec.Length && i < skillEmb.Length; i++)
                {
                    dot   += resumeVec[i] * skillEmb[i];
                    normR += resumeVec[i] * resumeVec[i];
                    normS += skillEmb[i]  * skillEmb[i];
                }
                var sim = (normR > 0 && normS > 0)
                    ? dot / (Math.Sqrt(normR) * Math.Sqrt(normS))
                    : 0.0;
                scored.Add((skillName, Math.Round(sim, 4)));
            }
            catch
            {
                scored.Add((skillName, 0.0));
            }
        }

        return scored
            .OrderByDescending(x => x.Similarity)
            .Take(topK)
            .Select(x => $"{x.SkillName} (cosine similarity: {x.Similarity:0.00})")
            .ToList();
    }

    [HttpGet("debug/graph/{skillName}")]
    public async Task<IActionResult> DebugGraphSkill(string skillName)
    {
        var neighborhood = await _graphService.GetValidNeighborhoodAsync(new[] { skillName });
        return Ok(new { skill = skillName, neighborhood });
    }

    [HttpPost("tailor-compare")]
    public async Task<IActionResult> TailorCompare([FromBody] TailorCompareRequest request)
    {
        if (request.ResumeId == Guid.Empty || request.JobId == Guid.Empty)
            return BadRequest("ResumeId and JobId are required.");

        var user = await _context.UserProfiles.FindAsync(request.ResumeId);
        var job  = await _context.JobPostings.FindAsync(request.JobId);

        if (user?.CleanSignal == null)
            return NotFound($"User profile {request.ResumeId} not found or has no CleanSignal.");
        if (job?.CleanSignal == null)
            return NotFound($"Job {request.JobId} not found or has no CleanSignal.");

        string rawResumeText;
        try
        {
            using var doc = JsonDocument.Parse(user.RawResume ?? "{}");
            rawResumeText = doc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : user.RawResume ?? "";
        }
        catch { rawResumeText = user.RawResume ?? ""; }
        var rawJobText = job.RawDescription ?? "";

        var baselineMatch = await _matchService.AnalyzeMatchAsync(request.ResumeId, request.JobId);
        if (baselineMatch == null)
            return NotFound("Match analysis failed.");

        var vectorRag = await RunVectorRagPipelineAsync(rawResumeText, rawJobText, user, baselineMatch, job.CleanSignal);
        var graphRag  = await RunGraphRagPipelineAsync(request.ResumeId, request.JobId, rawResumeText, rawJobText, baselineMatch, job.CleanSignal);

        static object ToResult(TailorPipelineResult r) => new
        {
            summaryBridgeRate       = r.SummaryBridgeRate,
            summarySemanticScore    = r.SummarySemanticScore,
            summarySemanticBaseline = r.SummarySemanticBaseline,
            hallucinationCount      = r.HallucinationCount,
            latencyMs               = r.LatencyMs,
            summaryText             = r.SummaryText,
            baselineGraphScore      = r.BaselineGraphScore,
            tailoredGraphScore      = r.TailoredGraphScore,
            graphScoreDeltaPercent  = Math.Round(r.GraphScoreDelta * 100.0, 2),
            pdfBase64               = Convert.ToBase64String(r.PdfBytes)
        };

        return Ok(new
        {
            resumeId  = request.ResumeId,
            jobId     = request.JobId,
            pipelineA = ToResult(vectorRag),
            pipelineB = ToResult(graphRag)
        });
    }

    private async Task<TailorPipelineResult> RunVectorRagPipelineAsync(
        string rawResume, string rawJob,
        UserProfile user,
        MatchAnalysisResult baselineMatch,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal jobSignal)
    {
        var sw = Stopwatch.StartNew();
        var jobTitle = jobSignal.TargetRoles?.FirstOrDefault()?.Title ?? "the role";
        var tailoredBullets = new List<TailoredBullet>();

        var refSkillNames = jobSignal.RequiredSkills
            .Select(s => s.Name)
            .ToList();
        var refSkillContext = string.Join(", ", refSkillNames);

        var experienceEntries = user.CleanSignal!.ExperienceSummary
            .Where(e => e.Bullets.Any(b => !string.IsNullOrWhiteSpace(b)))
            .ToList();

        foreach (var exp in experienceEntries)
        {
            var bulletsText = string.Join("\n", exp.Bullets.Select((b, i) => $"{i + 1}. {b}"));
            var prompt = $$"""
                You are an expert resume writer. Rewrite the bullets below for the target job.

                FULL RESUME:
                {{rawResume}}

                FULL JOB DESCRIPTION:
                {{rawJob}}

                RELEVANT SKILLS FOR THIS ROLE:
                {{refSkillContext}}

                EXPERIENCE ENTRY TO REWRITE:
                Role: {{exp.Role}} | Company: {{exp.Company}}
                {{bulletsText}}

                TASK: Rewrite each bullet to naturally surface relevant skills where they genuinely apply.
                Do not invent responsibilities.
                Use plain text only — no markdown, no asterisks, no bold, no italic, no bullet symbols.
                Return JSON array only: [{"original": "...", "rewritten": "..."}]
                """;

            tailoredBullets.AddRange(await ParseBulletsFromLlmAsync(prompt, exp));
        }

        var summaryPrompt = $$"""
            Write a tight 2 to 3 sentence professional summary for this candidate targeting: {{jobTitle}}.
            Write in third person. Do not include the candidate's name.
            No em-dashes, no hyphens used as dashes, no semicolons, no parentheses.
            Plain text only. Output only the summary paragraph.
            RESUME: {{rawResume}}
            JOB DESCRIPTION: {{rawJob}}
            """;
        string summary;
        try { summary = (await _chatClient.GetResponseAsync(summaryPrompt))?.Text?.Trim() ?? ""; }
        catch { summary = ""; }

        var personalInfo = ExtractPersonalInfoFromRawText(rawResume);
        var pdfBytes = _resumePdfService.GeneratePdf(personalInfo, user.CleanSignal, summary, tailoredBullets);

        var originalMappings = BuildOriginalMappings(user.CleanSignal, jobSignal);
        var canonicalSkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(tailoredBullets, summary, originalMappings);
        var scoring = _matchService.VerifiedMatchScore(baselineMatch, canonicalSkills, jobSignal);

        var summarySemanticScore    = await ComputeSemanticSimilarityAsync(summary, rawJob);
        var summarySemanticBaseline = await ComputeSemanticSimilarityAsync(rawResume, rawJob);
        var summaryBridgeRate       = await ComputeSummaryBridgeRate(summary, baselineMatch, user.CleanSignal, jobSignal);

        sw.Stop();
        return new TailorPipelineResult(
            summaryBridgeRate, summarySemanticScore, summarySemanticBaseline,
            scoring.HallucinationCount, sw.ElapsedMilliseconds, pdfBytes, summary,
            scoring.BaselineScore, scoring.VerifiedScore, scoring.Delta);
    }

    private async Task<TailorPipelineResult> RunGraphRagPipelineAsync(
        Guid resumeId, Guid jobId,
        string rawResume, string rawJob,
        MatchAnalysisResult baselineMatch,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal jobSignal)
    {
        var sw = Stopwatch.StartNew();

        var tailoredData = await _resumeService.BuildTailoredResumeDataAsync(resumeId, jobId, precomputedMatch: baselineMatch);
        if (tailoredData == null)
            return new TailorPipelineResult(0, 0, 0, 0, sw.ElapsedMilliseconds, [], "", 0, 0, 0);

        var pdfBytes = _resumePdfService.GeneratePdf(
            tailoredData.PersonalInfo, tailoredData.CleanSignal,
            tailoredData.ProfessionalSummary, tailoredData.TailoredBullets);

        var originalMappings = BuildOriginalMappings(tailoredData.CleanSignal, jobSignal);
        var canonicalSkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(
            tailoredData.TailoredBullets, tailoredData.ProfessionalSummary ?? "", originalMappings);
        var scoring = _matchService.VerifiedMatchScore(baselineMatch, canonicalSkills, jobSignal);

        var summarySemanticScore    = await ComputeSemanticSimilarityAsync(tailoredData.ProfessionalSummary, rawJob);
        var summarySemanticBaseline = await ComputeSemanticSimilarityAsync(rawResume, rawJob);
        var summaryBridgeRate       = await ComputeSummaryBridgeRate(tailoredData.ProfessionalSummary ?? "", baselineMatch, tailoredData.CleanSignal, jobSignal);

        sw.Stop();
        return new TailorPipelineResult(
            summaryBridgeRate, summarySemanticScore, summarySemanticBaseline,
            scoring.HallucinationCount, sw.ElapsedMilliseconds, pdfBytes,
            tailoredData.ProfessionalSummary ?? "",
            scoring.BaselineScore, scoring.VerifiedScore, scoring.Delta);
    }

    private async Task<double> ComputeSummaryBridgeRate(
        string summary,
        MatchAnalysisResult baseline,
        ARIS.Shared.Models.CleanSignal.ResumeCleanSignal? resumeSignal,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal jobSignal)
    {
        var total = baseline.PrerequisiteMetSkills.Count + baseline.BridgeableSkills.Count;
        if (total == 0) return 0.0;

        var originalMappings = BuildOriginalMappings(resumeSignal, jobSignal);
        var summarySkills = await _matchService.ExtractCanonicalSkillsFromTailoredTextAsync(
            [], summary, originalMappings);

        var t3t4Names = new HashSet<string>(
            baseline.PrerequisiteMetSkills.Select(s => s.SkillName)
                .Concat(baseline.BridgeableSkills.Select(s => s.SkillName)),
            StringComparer.OrdinalIgnoreCase);

        var bridgeCount = summarySkills.Count(s => t3t4Names.Contains(s));
        return Math.Round((double)bridgeCount / total, 4);
    }

    private async Task<List<TailoredBullet>> ParseBulletsFromLlmAsync(
        string prompt,
        ARIS.Shared.Models.CleanSignal.ExperienceSummary exp,
        float? temperature = null)
    {
        var results = new List<TailoredBullet>();
        try
        {
            var llmOptions = temperature.HasValue ? new ChatOptions { Temperature = temperature.Value } : null;
            var response = await _chatClient.GetResponseAsync(prompt, llmOptions);
            var text = response?.Text?.Trim() ?? "";
            var json = Regex.Replace(text, @"```(?:json)?", "").Trim();
            var startIdx = json.IndexOf('[');
            var endIdx = json.LastIndexOf(']');
            if (startIdx >= 0 && endIdx > startIdx)
                json = json[startIdx..(endIdx + 1)];

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<List<BulletItem>>(json, options);
            if (parsed != null)
            {
                foreach (var item in parsed)
                {
                    if (!string.IsNullOrWhiteSpace(item.Original) && !string.IsNullOrWhiteSpace(item.Rewritten))
                    {
                        results.Add(new TailoredBullet
                        {
                            OriginalBullet  = item.Original,
                            RewrittenBullet = item.Rewritten,
                            TargetSkill     = exp.Role,
                            Role            = exp.Role,
                            Company         = exp.Company,
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse bullets from LLM for role {Role}", exp.Role);
            foreach (var bullet in exp.Bullets.Where(b => !string.IsNullOrWhiteSpace(b)))
            {
                results.Add(new TailoredBullet
                {
                    OriginalBullet  = bullet,
                    RewrittenBullet = bullet,
                    TargetSkill     = exp.Role,
                    Role            = exp.Role,
                    Company         = exp.Company,
                });
            }
        }
        return results;
    }

    private record BulletItem(string Original, string Rewritten);

    private static IEnumerable<(string Original, string Canonical)> BuildOriginalMappings(
        ARIS.Shared.Models.CleanSignal.ResumeCleanSignal? resumeSignal,
        ARIS.Shared.Models.CleanSignal.JobPostingCleanSignal? jobSignal)
    {
        var mappings = new List<(string, string)>();

        if (resumeSignal != null)
        {
            foreach (var s in resumeSignal.Skills.Where(s => s.OriginalName != null))
                mappings.Add((s.OriginalName!, s.Name));
        }

        if (jobSignal != null)
        {
            foreach (var s in jobSignal.RequiredSkills.Where(s => s.OriginalName != null))
                mappings.Add((s.OriginalName!, s.Name));
        }

        return mappings;
    }

    private static PersonalInfo ExtractPersonalInfoFromRawText(string rawResume)
    {
        var lines = rawResume.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var name = lines.FirstOrDefault()?.Trim() ?? "Candidate";
        return new PersonalInfo { Name = name };
    }

    private async Task<double> ComputeSemanticSimilarityAsync(string? text, string jobText)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(jobText))
            return 0.0;

        var textEmb = await _resumeService.GenerateEmbeddingAsync(text);
        var jobEmb  = await _resumeService.GenerateEmbeddingAsync(jobText);

        double dot = 0, normT = 0, normJ = 0;
        for (int i = 0; i < textEmb.Length; i++)
        {
            dot   += textEmb[i] * jobEmb[i];
            normT += textEmb[i] * textEmb[i];
            normJ += jobEmb[i]  * jobEmb[i];
        }
        return (normT > 0 && normJ > 0) ? Math.Round(dot / (Math.Sqrt(normT) * Math.Sqrt(normJ)), 4) : 0.0;
    }
}
