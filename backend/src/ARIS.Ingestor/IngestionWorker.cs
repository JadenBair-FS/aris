using ARIS.Ingestor.Services;
using ARIS.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using ARIS.Shared.Data;
using Pgvector.EntityFrameworkCore;
using ARIS.Shared.Models.Ingestion.Roadmap;

using PgVectorType = Pgvector.Vector;

namespace ARIS.Ingestor;

public class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IngestionWorker> _logger;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;

    public IngestionWorker(
        IServiceProvider serviceProvider,
        ILogger<IngestionWorker> logger,
        IHostApplicationLifetime hostApplicationLifetime,
        IEmbeddingGenerator<string, Embedding<float>> embeddingService)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _hostApplicationLifetime = hostApplicationLifetime;
        _embeddingService = embeddingService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Ingestion Worker Started.");

        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ArisDbContext>();
        var onetService = scope.ServiceProvider.GetRequiredService<OnetService>();
        var roadmapService = scope.ServiceProvider.GetRequiredService<RoadmapService>();
        var neo4jService = scope.ServiceProvider.GetRequiredService<Neo4jIngestionService>();
        var ontologyService = scope.ServiceProvider.GetRequiredService<OntologyEnrichmentService>();

        var args = Environment.GetCommandLineArgs();

        if (args.Contains("--restore-roadmap"))
        {
            _logger.LogInformation(">>> RESTORE ROADMAP MODE <<<");
            await RestoreRoadmapFoundationsAsync(roadmapService, neo4jService, dbContext, stoppingToken);
            _logger.LogInformation("Roadmap Foundations Restored.");
            _hostApplicationLifetime.StopApplication();
            return;
        }
        
        if (args.Contains("--optimize-graph"))
        {
            int? limit = null;
            var limitIndex = Array.IndexOf(args, "--limit");
            if (limitIndex >= 0 && limitIndex + 1 < args.Length && int.TryParse(args[limitIndex + 1], out var parsedLimit))
            {
                limit = parsedLimit;
            }

            var skipBridges = args.Contains("--skip-bridges");
            var skipDeps = args.Contains("--skip-deps");
            var skipPass1 = args.Contains("--skip-pass1");
            
            int resumeIndex = 0;
            var resumeIdx = Array.IndexOf(args, "--resume");
            if (resumeIdx >= 0 && resumeIdx + 1 < args.Length && int.TryParse(args[resumeIdx + 1], out var parsedResume))
            {
                resumeIndex = parsedResume;
            }

            _logger.LogInformation(">>> OPTIMIZATION MODE <<<");
            if (resumeIndex > 0) _logger.LogInformation("Resuming from index: {ResumeIndex}", resumeIndex);

            var processedBridges = new HashSet<string>();
            var processedDeps = new HashSet<string>();

            if (!skipBridges)
            {
                if (!skipPass1)
                {
                    _logger.LogInformation("=== Pass 1: Sibling Bridge Detection ===");
                    var siblings = await neo4jService.GetSiblingClustersAsync();
                    var clusterCount = 0;
                    foreach (var cluster in siblings)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        if (cluster.Siblings.Count > 1)
                        {
                            clusterCount++;
                            _logger.LogInformation("[Sibling Cluster {Current}/{Total}] Analyzing '{Parent}' with {Count} siblings",
                                clusterCount, siblings.Count, cluster.Parent, cluster.Siblings.Count);

                            var hierarchies = await neo4jService.GetInternalHierarchiesAsync(cluster.Siblings);
                            var parentNames = hierarchies.Select(h => h.Parent).ToHashSet();
                            var prunedSkills = cluster.Siblings.Where(s => !parentNames.Contains(s)).ToList();

                            if (prunedSkills.Count < 2) 
                            {
                                _logger.LogInformation("  - Skipping: Pruning left less than 2 skills.");
                                continue;
                            }

                            var bridges = await ontologyService.FindBridgesAsync(cluster.Parent, prunedSkills, hierarchies, stoppingToken);
                            foreach (var bridge in bridges)
                            {
                                var key = string.Join("|", new[] { bridge.Source, bridge.Target }.OrderBy(s => s));
                                if (processedBridges.Add(key))
                                {
                                    _logger.LogInformation("  + New Bridge: {Source} <-> {Target}", bridge.Source, bridge.Target);
                                    await neo4jService.MergeBridgeRelationshipAsync(bridge.Source, bridge.Target);
                                }
                            }
                        }
                    }
                }

                _logger.LogInformation("=== Pass 2: Role-Based Bridge Detection ===");
                var roles = await neo4jService.GetRolesWithSkillsAsync();
                _logger.LogInformation("Found {Count} roles to analyze in Pass 2.", roles.Count);
                var processedSkillHashes = new HashSet<string>();
                var processedCount = 0;

                foreach (var role in roles)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (limit.HasValue && processedCount >= limit.Value) break;

                    var techSkills = role.Skills.Where(s => s.Length < 60).OrderBy(s => s).ToList();
                    var skillHash = string.Join(",", techSkills);

                    if (techSkills.Count > 1 && processedSkillHashes.Add(skillHash))
                    {
                        processedCount++;
                        if (processedCount < resumeIndex) 
                        {
                            if (processedCount % 100 == 0 || processedCount == resumeIndex - 1)
                                _logger.LogInformation("  [Pass 2] Skipping role {Current}/{ResumeIndex}...", processedCount, resumeIndex);
                            continue;
                        }

                        _logger.LogInformation("[Role Bridge {Current}/{Total}] Analyzing '{Role}' with {Count} skills",
                            processedCount, limit ?? roles.Count, role.RoleTitle, techSkills.Count);

                        var hierarchies = await neo4jService.GetInternalHierarchiesAsync(techSkills);
                        var parentNames = hierarchies.Select(h => h.Parent).ToHashSet();
                        var prunedSkills = techSkills.Where(s => !parentNames.Contains(s)).ToList();

                        if (prunedSkills.Count < 2)
                        {
                            _logger.LogInformation("  - Skipping: Pruning left less than 2 skills.");
                            continue;
                        }

                        var bridges = await ontologyService.FindBridgesAsync(role.RoleTitle, prunedSkills, hierarchies, stoppingToken);
                        foreach (var bridge in bridges)
                        {
                            var key = string.Join("|", new[] { bridge.Source, bridge.Target }.OrderBy(s => s));
                            if (processedBridges.Add(key))
                            {
                                _logger.LogInformation("  + New Bridge: {Source} <-> {Target}", bridge.Source, bridge.Target);
                                await neo4jService.MergeBridgeRelationshipAsync(bridge.Source, bridge.Target);
                            }
                        }
                    }
                }
            }

            if (!skipDeps)
            {
                _logger.LogInformation("=== Pass 3: Dependency Detection ===");
                var roles = await neo4jService.GetRolesWithSkillsAsync();
                _logger.LogInformation("Found {Count} roles to analyze in Pass 3.", roles.Count);
                var processedSkillHashes = new HashSet<string>();
                var processedCount = 0;

                foreach (var role in roles)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (limit.HasValue && processedCount >= limit.Value) break;

                    var techSkills = role.Skills.Where(s => s.Length < 60).OrderBy(s => s).ToList();
                    var skillHash = string.Join(",", techSkills);

                    if (techSkills.Count > 1 && processedSkillHashes.Add(skillHash))
                    {
                        processedCount++;
                        // If we are resuming and didn't skip bridges, Pass 3 should probably start fresh.
                        // But if we DID skip bridges, maybe they want to resume Pass 3?
                        // For simplicity, let's only resume Pass 3 if --resume-p3 is used OR if pass 2 was skipped.
                        var p3ResumeIndex = args.Contains("--resume-p3") ? int.Parse(args[Array.IndexOf(args, "--resume-p3") + 1]) : 0;
                        if (skipBridges && resumeIndex > 0) p3ResumeIndex = resumeIndex;

                        if (processedCount < p3ResumeIndex)
                        {
                            if (processedCount % 100 == 0 || processedCount == p3ResumeIndex - 1)
                                _logger.LogInformation("  [Pass 3] Skipping role {Current}/{ResumeIndex}...", processedCount, p3ResumeIndex);
                            continue;
                        }

                        _logger.LogInformation("[Dependency {Current}/{Total}] Analyzing '{Role}' with {Count} skills",
                            processedCount, limit ?? roles.Count, role.RoleTitle, techSkills.Count);

                        var deps = await ontologyService.FindDependenciesAsync(role.RoleTitle, techSkills, stoppingToken);
                        foreach (var dep in deps)
                        {
                            var key = $"{dep.Child}|{dep.Parent}";
                            if (processedDeps.Add(key))
                            {
                                _logger.LogInformation("  + New Dependency: {Child} -> {Parent}", dep.Child, dep.Parent);
                                await neo4jService.MergeSubsetRelationshipAsync(dep.Child, dep.Parent);
                            }
                        }
                    }
                }
            }

            _logger.LogInformation("Optimization Complete.");
            _hostApplicationLifetime.StopApplication();
            return;
        }

        await dbContext.Database.EnsureCreatedAsync(stoppingToken);

        await neo4jService.EnsureIndicesAsync();

        //await EnsureCleanSlateAsync(dbContext, neo4jService, stoppingToken);

        var roleRoadmapSlugs = new[]
        {
            "frontend", "backend", "full-stack", "devops", "devsecops",
            "android", "ios", "game-developer", "server-side-game-developer",
            "ai-engineer", "ai-data-scientist", "ai-agents", "ai-red-teaming",
            "machine-learning", "mlops", "data-analyst", "data-engineer", "bi-analyst",
            "qa", "cyber-security", "blockchain",
            "software-architect", "ux-design", "technical-writer",
            "product-manager", "engineering-manager", "devrel"
        };

        var skillRoadmapSlugs = new[]
        {
            "computer-science", "system-design", "software-design-architecture",
            "datastructures-and-algorithms", "design-system", "prompt-engineering",
            "javascript", "typescript", "python", "java", "cpp", "rust", "golang",
            "php", "ruby", "kotlin", "sql", "graphql",
            "react", "react-native", "angular", "vue", "nextjs", "swift-ui",
            "nodejs", "aspnet-core", "spring-boot", "django", "laravel", "ruby-on-rails",
            "flutter", "html", "css", "shell-bash",
            "postgresql-dba", "mongodb", "redis", "elasticsearch",
            "docker", "kubernetes", "aws", "terraform", "cloudflare",
            "linux", "git-github", "wordpress"
        };

        foreach (var slug in roleRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Processing Role Roadmap: {Slug}", slug);
            var roadmap = await roadmapService.GetRoadmapAsync(slug, stoppingToken);
            if (roadmap?.Nodes == null) continue;

            var roleTitle = roadmap.Title?.Card ?? slug;
            var roleCode = $"ROADMAP_{slug.ToUpper().Replace("-", "_")}";
            await neo4jService.MergeRoleAsync(roleTitle, roleCode, $"Industry standard roadmap for {roleTitle}");

            foreach (var node in roadmap.Nodes)
            {
                if (node.Type is not ("topic" or "subtopic")) continue;
                var canonicalName = await ProcessRoadmapNodeAsync(dbContext, neo4jService, node, stoppingToken);
                if (!string.IsNullOrEmpty(canonicalName))
                {
                    await neo4jService.MergeRoleSkillRelationshipAsync(roleCode, canonicalName);
                }
            }
            await Task.Delay(500, stoppingToken);
        }

        foreach (var slug in skillRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Processing Skill Roadmap: {Slug}", slug);
            var roadmap = await roadmapService.GetRoadmapAsync(slug, stoppingToken);
            if (roadmap?.Nodes == null) continue;

            var parentSkillName = roadmap.Title?.Card ?? slug;

            foreach (var node in roadmap.Nodes)
            {
                if (node.Type is not ("topic" or "subtopic")) continue;
                var childCanonicalName = await ProcessRoadmapNodeAsync(dbContext, neo4jService, node, stoppingToken);

                if (!string.IsNullOrEmpty(childCanonicalName) &&
                    !string.Equals(childCanonicalName, parentSkillName, StringComparison.OrdinalIgnoreCase))
                {
                    await neo4jService.MergeSubsetRelationshipAsync(childCanonicalName, parentSkillName);
                }
            }
            await Task.Delay(500, stoppingToken);
        }

        _logger.LogInformation("Fetching Occupations from O*NET...");
        var occupations = await onetService.GetAllOccupationsAsync(stoppingToken);
        _logger.LogInformation("Found {Count} occupations.", occupations.Count);

        foreach (var occDto in occupations)
        {
            if (stoppingToken.IsCancellationRequested) break;

            _logger.LogInformation("Processing: {Title} ({Code})", occDto.Title, occDto.Code);

            var details = await onetService.GetOccupationDetailsAsync(occDto.Code, stoppingToken);
            if (details == null) continue;

            await neo4jService.MergeRoleAsync(details.Title, details.Code, details.Description ?? "");

            var existingRole = await dbContext.Roles
                .FirstOrDefaultAsync(r => r.OnetCode == occDto.Code, stoppingToken);

            if (existingRole == null)
            {
                var roleText = $"{details.Title}: {details.Description}";
                var roleEmbedding = await GenerateEmbeddingAsync(roleText);

                var role = new RefRole
                {
                    Title = details.Title,
                    OnetCode = details.Code,
                    Description = details.Description,
                    Embedding = roleEmbedding
                };

                dbContext.Roles.Add(role);
                await dbContext.SaveChangesAsync(stoppingToken);

                existingRole = role;
            }

            if (details.Skills != null)
            {
                foreach (var skillName in details.Skills)
                {
                    await ProcessSkillAsync(dbContext, neo4jService, existingRole.Id, details.Code, skillName, "ONET_Skill", stoppingToken);
                }
            }

            await dbContext.SaveChangesAsync(stoppingToken);
            await Task.Delay(200, stoppingToken);
        }

        _logger.LogInformation("Ingestion Complete.");
        _hostApplicationLifetime.StopApplication();
    }

    private async Task RestoreRoadmapFoundationsAsync(RoadmapService roadmapService, Neo4jIngestionService neo4jService, ArisDbContext dbContext, CancellationToken stoppingToken)
    {
        var roleRoadmapSlugs = new[]
        {
            "frontend", "backend", "full-stack", "devops", "devsecops",
            "android", "ios", "game-developer", "server-side-game-developer",
            "ai-engineer", "ai-data-scientist", "ai-agents", "ai-red-teaming",
            "machine-learning", "mlops", "data-analyst", "data-engineer", "bi-analyst",
            "qa", "cyber-security", "blockchain",
            "software-architect", "ux-design", "technical-writer",
            "product-manager", "engineering-manager", "devrel"
        };

        var skillRoadmapSlugs = new[]
        {
            "computer-science", "system-design", "software-design-architecture",
            "datastructures-and-algorithms", "design-system", "prompt-engineering",
            "javascript", "typescript", "python", "java", "cpp", "rust", "golang",
            "php", "ruby", "kotlin", "sql", "graphql",
            "react", "react-native", "angular", "vue", "nextjs", "swift-ui",
            "nodejs", "aspnet-core", "spring-boot", "django", "laravel", "ruby-on-rails",
            "flutter", "html", "css", "shell-bash",
            "postgresql-dba", "mongodb", "redis", "elasticsearch",
            "docker", "kubernetes", "aws", "terraform", "cloudflare",
            "linux", "git-github", "wordpress"
        };

        foreach (var slug in roleRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Restoring Role Roadmap: {Slug}", slug);
            var roadmap = await roadmapService.GetRoadmapAsync(slug, stoppingToken);
            if (roadmap?.Nodes == null) continue;

            var roleTitle = roadmap.Title?.Card ?? slug;
            var roleCode = $"ROADMAP_{slug.ToUpper().Replace("-", "_")}";
            await neo4jService.MergeRoleAsync(roleTitle, roleCode, $"Industry standard roadmap for {roleTitle}");

            foreach (var node in roadmap.Nodes)
            {
                if (node.Type is not ("topic" or "subtopic")) continue;
                var canonicalName = await ProcessRoadmapNodeAsync(dbContext, neo4jService, node, stoppingToken);
                if (!string.IsNullOrEmpty(canonicalName))
                {
                    await neo4jService.MergeRoleSkillRelationshipAsync(roleCode, canonicalName);
                }
            }
        }

        foreach (var slug in skillRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Restoring Skill Roadmap Relationships: {Slug}", slug);
            var roadmap = await roadmapService.GetRoadmapAsync(slug, stoppingToken);
            if (roadmap?.Nodes == null) continue;

            var parentSkillName = roadmap.Title?.Card ?? slug;

            foreach (var node in roadmap.Nodes)
            {
                if (node.Type is not ("topic" or "subtopic")) continue;
                var childCanonicalName = await ProcessRoadmapNodeAsync(dbContext, neo4jService, node, stoppingToken);

                if (!string.IsNullOrEmpty(childCanonicalName) &&
                    !string.Equals(childCanonicalName, parentSkillName, StringComparison.OrdinalIgnoreCase))
                {
                    await neo4jService.MergeSubsetRelationshipAsync(childCanonicalName, parentSkillName);
                }
            }
        }
    }

    private async Task EnsureCleanSlateAsync(ArisDbContext dbContext, Neo4jIngestionService neo4j, CancellationToken ct)
    {
        _logger.LogWarning("!!! CLEARING ALL DATA !!!");

        await dbContext.Database.ExecuteSqlRawAsync("TRUNCATE TABLE ref_role_skills, ref_skills, ref_roles RESTART IDENTITY CASCADE;", ct);
        _logger.LogInformation("Postgres Dictionary Tables Truncated.");

        // Clear Neo4j
        await neo4j.ClearDatabaseAsync();
    }


    private async Task<string> ProcessRoadmapNodeAsync(ArisDbContext dbContext, Neo4jIngestionService neo4j, RoadmapNodeDto node, CancellationToken ct)
    {
        var title = node.Data?.Label;
        if (string.IsNullOrEmpty(title)) return string.Empty;

        var embedding = await GenerateEmbeddingAsync(title);
        if (embedding == null) return string.Empty;

        var canonicalSkill = await dbContext.Skills
            .Where(s => s.Embedding != null)
            .Select(s => new { Skill = s, Distance = s.Embedding!.CosineDistance(embedding) })
            .Where(x => x.Distance < 0.15)
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync(ct);

        string canonicalName = title;

        if (canonicalSkill != null)
        {
            canonicalName = canonicalSkill.Skill.Name;
            _logger.LogInformation("Deduplicated: '{Raw}' -> '{Canonical}' (Dist: {Dist:F3})", title, canonicalName, canonicalSkill.Distance);
        }
        else
        {
            var skill = new RefSkill
            {
                Name = title,
                Source = "Roadmap.sh",
                Embedding = embedding
            };
            dbContext.Skills.Add(skill);
            await dbContext.SaveChangesAsync(ct);
        }

        await neo4j.MergeSkillAsync(canonicalName, "Roadmap.sh");
        return canonicalName;
    }

    private async Task ProcessSkillAsync(ArisDbContext dbContext, Neo4jIngestionService neo4j, int roleId, string roleOnetCode, string skillName, string source, CancellationToken ct)
    {
        var embedding = await GenerateEmbeddingAsync(skillName);
        if (embedding == null) return;

        var canonicalMatch = await dbContext.Skills
            .Where(s => s.Embedding != null)
            .Select(s => new { Skill = s, Distance = s.Embedding!.CosineDistance(embedding) })
            .Where(x => x.Distance < 0.15)
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync(ct);

        RefSkill skill;
        string canonicalName = skillName;

        if (canonicalMatch != null)
        {
            skill = canonicalMatch.Skill;
            canonicalName = skill.Name;
            _logger.LogInformation("Deduplicated: '{Raw}' -> '{Canonical}' (Dist: {Dist:F3})", skillName, canonicalName, canonicalMatch.Distance);
        }
        else
        {
            skill = new RefSkill
            {
                Name = skillName,
                Source = source,
                Embedding = embedding
            };
            dbContext.Skills.Add(skill);
            await dbContext.SaveChangesAsync(ct);
        }

        await neo4j.MergeSkillAsync(canonicalName, source);
        await neo4j.MergeRoleSkillRelationshipAsync(roleOnetCode, canonicalName);

        var roleSkill = await dbContext.RoleSkills
            .FirstOrDefaultAsync(rs => rs.RoleId == roleId && rs.SkillId == skill.Id, ct);

        if (roleSkill == null)
        {
            roleSkill = new RefRoleSkill
            {
                RoleId = roleId,
                SkillId = skill.Id,
                Importance = 0,
                Level = 0
            };
            dbContext.RoleSkills.Add(roleSkill);
        }
    }

    private async Task<PgVectorType?> GenerateEmbeddingAsync(string text)
    {
        try
        {
            var embeddings = await _embeddingService.GenerateAsync([text]);
            var vectorData = embeddings[0].Vector;
            return new PgVectorType(vectorData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate embedding for textd: {Text}", text.Length > 50 ? text.Substring(0, 50) + "..." : text);
            return null;
        }
    }
}