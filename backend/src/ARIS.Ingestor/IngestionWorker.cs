using ARIS.Ingestor.Services;
using ARIS.Shared.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using ARIS.Shared.Data;
using Pgvector.EntityFrameworkCore;

using PgVectorType = Pgvector.Vector;

namespace ARIS.Ingestor;

public class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IngestionWorker> _logger;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;

    private const double DedupThreshold = 0.10;

    // Valid node types in Roadmap.sh JSON that represent learnable skills
    private static readonly HashSet<string> ValidRoadmapNodeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "topic", "subtopic", "skill" };

    private static readonly string[] RoleRoadmapSlugs =
    [
        "frontend", "backend", "full-stack", "devops", "devsecops",
        "android", "ios", "game-developer", "server-side-game-developer",
        "ai-engineer", "ai-data-scientist", "ai-agents", "ai-red-teaming",
        "machine-learning", "mlops", "data-analyst", "data-engineer", "bi-analyst",
        "qa", "cyber-security", "blockchain",
        "software-architect", "ux-design", "technical-writer",
        "product-manager", "engineering-manager", "devrel"
    ];

    private static readonly string[] SkillRoadmapSlugs =
    [
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
    ];

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

        // ── Special Modes ──────────────────────────────────────────────────────

        if (args.Contains("--optimize-graph"))
        {
            _logger.LogInformation(">>> OPTIMIZATION MODE <<<");
            await RunOptimizeGraphAsync(args, neo4jService, ontologyService, stoppingToken);
            _logger.LogInformation("Optimization Complete.");
            _hostApplicationLifetime.StopApplication();
            return;
        }

        if (args.Contains("--seed-gold"))
        {
            _logger.LogInformation(">>> GOLD STANDARD SEEDING MODE <<<");
            var seeder = scope.ServiceProvider.GetRequiredService<GoldStandardSeeder>();
            await seeder.RunSeedingAsync("C:\\dev\\Masters Capstone\\GoldStandard");
            _logger.LogInformation("Gold Standard Seeding Complete.");
            _hostApplicationLifetime.StopApplication();
            return;
        }

        // ── Main Ingestion ─────────────────────────────────────────────────────

        await dbContext.Database.EnsureCreatedAsync(stoppingToken);
        await neo4jService.EnsureIndicesAsync();

        if (args.Contains("--fresh"))
        {
            _logger.LogWarning(">>> FRESH MODE: Clearing all data <<<");
            await EnsureCleanSlateAsync(dbContext, neo4jService, stoppingToken);
        }

        // Phase 1: O*NET — anchor taxonomy (roles + skills, IsTech from endpoint type)
        _logger.LogInformation("=== Phase 1: O*NET Ingestion ===");
        await IngestOnetAsync(dbContext, onetService, neo4jService, stoppingToken);

        // Phase 2: Build slug → O*NET role map (cosine similarity, threshold 0.30, top-3)
        _logger.LogInformation("=== Phase 2: Building Slug-Role Map ===");
        var slugRoleMap = await BuildSlugRoleMapAsync(dbContext, roadmapService, RoleRoadmapSlugs, stoppingToken);

        // Phase 3: Role roadmaps — tech skill enhancement + real edge hierarchy + REQUIRES links
        _logger.LogInformation("=== Phase 3: Role Roadmap Ingestion ===");
        foreach (var slug in RoleRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Processing Role Roadmap: {Slug}", slug);
            var matchedRoles = slugRoleMap.GetValueOrDefault(slug) ?? [];
            await IngestRoadmapAsync(dbContext, roadmapService, neo4jService, slug,
                isRoleRoadmap: true, matchedRoles, stoppingToken);
        }

        // Phase 4: Skill roadmaps — standalone SUBSET_OF trees rooted at roadmap title
        _logger.LogInformation("=== Phase 4: Skill Roadmap Ingestion ===");
        foreach (var slug in SkillRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Processing Skill Roadmap: {Slug}", slug);
            await IngestRoadmapAsync(dbContext, roadmapService, neo4jService, slug,
                isRoleRoadmap: false, [], stoppingToken);
        }

        _logger.LogInformation("Ingestion Complete.");
        _hostApplicationLifetime.StopApplication();
    }

    // ── Phase 1: O*NET ─────────────────────────────────────────────────────────

    private async Task IngestOnetAsync(
        ArisDbContext dbContext,
        OnetService onetService,
        Neo4jIngestionService neo4j,
        CancellationToken ct)
    {
        var occupations = await onetService.GetAllOccupationsAsync(ct);
        _logger.LogInformation("Found {Count} O*NET occupations.", occupations.Count);

        var i = 0;
        foreach (var occ in occupations)
        {
            if (ct.IsCancellationRequested) break;
            i++;
            _logger.LogInformation("[O*NET {Current}/{Total}] {Title} ({Code})",
                i, occupations.Count, occ.Title, occ.Code);

            var details = await onetService.GetOccupationDetailsAsync(occ.Code, ct);
            if (details == null) continue;

            await neo4j.MergeRoleAsync(details.Title, details.Code, details.Description ?? "");

            var existingRole = await dbContext.Roles
                .FirstOrDefaultAsync(r => r.OnetCode == occ.Code, ct);

            if (existingRole == null)
            {
                var roleEmbedding = await GenerateEmbeddingAsync($"{details.Title}: {details.Description}");
                existingRole = new RefRole
                {
                    Title = details.Title,
                    OnetCode = details.Code,
                    Description = details.Description,
                    Embedding = roleEmbedding
                };
                dbContext.Roles.Add(existingRole);
                await dbContext.SaveChangesAsync(ct);
            }

            // Cognitive / psychomotor / sensory skills → IsTech = false
            foreach (var skillName in details.Skills)
            {
                var canon = await DeduplicateOrCreateSkillAsync(
                    dbContext, neo4j, skillName, "ONET_Skill", isTech: false, ct);
                await neo4j.MergeRoleSkillRelationshipAsync(details.Code, canon);
                await LinkSkillToRoleInPostgresAsync(dbContext, existingRole.Id, canon, ct);
            }

            // Domain knowledge areas (e.g. "Building and Construction", "Customer Service") → IsTech = false
            foreach (var knowledgeName in details.Knowledge)
            {
                var canon = await DeduplicateOrCreateSkillAsync(
                    dbContext, neo4j, knowledgeName, "ONET_Skill", isTech: false, ct);
                await neo4j.MergeRoleSkillRelationshipAsync(details.Code, canon);
                await LinkSkillToRoleInPostgresAsync(dbContext, existingRole.Id, canon, ct);
            }

            // Work activities (on-the-job tasks, e.g. "Inspecting Equipment") → IsTech = false
            foreach (var activityName in details.WorkActivities)
            {
                var canon = await DeduplicateOrCreateSkillAsync(
                    dbContext, neo4j, activityName, "ONET_Skill", isTech: false, ct);
                await neo4j.MergeRoleSkillRelationshipAsync(details.Code, canon);
                await LinkSkillToRoleInPostgresAsync(dbContext, existingRole.Id, canon, ct);
            }

            // Technology skills (specific software / tools) → IsTech = true
            foreach (var techSkillName in details.TechnologySkills)
            {
                var canon = await DeduplicateOrCreateSkillAsync(
                    dbContext, neo4j, techSkillName, "ONET_Skill", isTech: true, ct);
                await neo4j.MergeRoleSkillRelationshipAsync(details.Code, canon);
                await LinkSkillToRoleInPostgresAsync(dbContext, existingRole.Id, canon, ct);
            }

            await dbContext.SaveChangesAsync(ct);
            await Task.Delay(200, ct);
        }

        _logger.LogInformation("O*NET Ingestion Complete: {Count} occupations processed.", i);
    }

    // ── Phase 2: Build Slug → Role Map ─────────────────────────────────────────

    private async Task<Dictionary<string, List<string>>> BuildSlugRoleMapAsync(
        ArisDbContext dbContext,
        RoadmapService roadmapService,
        string[] slugs,
        CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var slug in slugs)
        {
            if (ct.IsCancellationRequested) break;

            var roadmap = await roadmapService.GetRoadmapAsync(slug, ct);
            var title = roadmap?.Title?.Card ?? slug.Replace("-", " ");

            var embedding = await GenerateEmbeddingAsync(title);
            if (embedding == null)
            {
                map[slug] = [];
                continue;
            }

            var matches = await dbContext.Roles
                .Where(r => r.Embedding != null)
                .Select(r => new { r.OnetCode, r.Title, Distance = r.Embedding!.CosineDistance(embedding) })
                .Where(x => x.Distance < 0.30)
                .OrderBy(x => x.Distance)
                .Take(3)
                .ToListAsync(ct);

            map[slug] = matches.Select(m => m.OnetCode).OfType<string>().ToList();
            _logger.LogInformation("Slug '{Slug}' ({Title}) → {Count} O*NET roles: {Roles}",
                slug, title, map[slug].Count,
                string.Join(", ", matches.Select(m => $"{m.Title} ({m.OnetCode})")));

            await Task.Delay(100, ct);
        }

        return map;
    }

    // ── Phases 3 + 4: Roadmap Ingestion ────────────────────────────────────────

    private async Task IngestRoadmapAsync(
        ArisDbContext dbContext,
        RoadmapService roadmapService,
        Neo4jIngestionService neo4j,
        string slug,
        bool isRoleRoadmap,
        List<string> matchedRoleCodes,
        CancellationToken ct)
    {
        var roadmap = await roadmapService.GetRoadmapAsync(slug, ct);
        if (roadmap?.Nodes == null)
        {
            _logger.LogWarning("Roadmap '{Slug}' returned no nodes — skipping.", slug);
            return;
        }

        // Step 1: Process all valid nodes → build nodeId → canonical skill name map
        var nodeIdToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in roadmap.Nodes)
        {
            if (!ValidRoadmapNodeTypes.Contains(node.Type ?? "")) continue;
            var label = node.Data?.Label?.Trim();
            if (string.IsNullOrWhiteSpace(label) || node.Id == null) continue;

            var canon = await DeduplicateOrCreateSkillAsync(
                dbContext, neo4j, label, "Roadmap.sh", isTech: true, ct);
            if (!string.IsNullOrEmpty(canon))
                nodeIdToCanonical[node.Id] = canon;
        }

        _logger.LogInformation("Roadmap '{Slug}': {Count} valid skills processed.", slug, nodeIdToCanonical.Count);

        // Step 2: Process edges for hierarchy (solid → SUBSET_OF) and bridges (dashed → BRIDGE_TO)
        var validNodeIds = nodeIdToCanonical.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var solidEdgeTargetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (roadmap.Edges != null)
        {
            foreach (var edge in roadmap.Edges)
            {
                if (edge.Source == null || edge.Target == null) continue;
                if (!validNodeIds.Contains(edge.Source) || !validNodeIds.Contains(edge.Target)) continue;

                var parentCanon = nodeIdToCanonical[edge.Source];
                var childCanon = nodeIdToCanonical[edge.Target];

                if (string.Equals(parentCanon, childCanon, StringComparison.OrdinalIgnoreCase)) continue;

                var edgeStyle = edge.Data?.EdgeStyle ?? "solid";
                if (edgeStyle.Equals("dashed", StringComparison.OrdinalIgnoreCase))
                {
                    // Dashed edge = alternative/optional → BRIDGE_TO
                    await neo4j.MergeBridgeRelationshipAsync(parentCanon, childCanon, "Roadmap.sh");
                }
                else
                {
                    // Solid edge = parent → child dependency → child SUBSET_OF parent
                    await neo4j.MergeSubsetRelationshipAsync(childCanon, parentCanon, "Roadmap.sh");
                    solidEdgeTargetIds.Add(edge.Target);
                }
            }
        }

        if (!isRoleRoadmap)
        {
            // Skill roadmap: top-level nodes (no solid edge parent in this roadmap) link to root skill
            var rootSkillName = roadmap.Title?.Card ?? slug.Replace("-", " ");
            await DeduplicateOrCreateSkillAsync(dbContext, neo4j, rootSkillName, "Roadmap.sh", isTech: true, ct);

            foreach (var (nodeId, canonName) in nodeIdToCanonical)
            {
                if (solidEdgeTargetIds.Contains(nodeId)) continue;
                if (string.Equals(canonName, rootSkillName, StringComparison.OrdinalIgnoreCase)) continue;
                await neo4j.MergeSubsetRelationshipAsync(canonName, rootSkillName, "Roadmap.sh");
            }
        }
        else
        {
            // Role roadmap: link all skills to each matched O*NET role via REQUIRES
            if (matchedRoleCodes.Count == 0)
            {
                _logger.LogWarning(
                    "Roadmap '{Slug}' has no matched O*NET roles — skills ingested without REQUIRES links.", slug);
            }

            foreach (var (_, canonName) in nodeIdToCanonical)
            {
                foreach (var roleCode in matchedRoleCodes)
                {
                    await neo4j.MergeRoleSkillRelationshipAsync(roleCode, canonName);
                }
            }
        }

        await Task.Delay(500, ct);
    }

    // ── Shared Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Central deduplication + upsert helper. Cosine distance threshold = 0.10.
    /// IsTech = true wins and is never downgraded.
    /// Returns the canonical skill name to use for all graph relationships.
    /// </summary>
    private async Task<string> DeduplicateOrCreateSkillAsync(
        ArisDbContext dbContext,
        Neo4jIngestionService neo4j,
        string name,
        string source,
        bool isTech,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var embedding = await GenerateEmbeddingAsync(name);
        if (embedding == null) return name;

        var canonicalMatch = await dbContext.Skills
            .Where(s => s.Embedding != null)
            .Select(s => new { Skill = s, Distance = s.Embedding!.CosineDistance(embedding) })
            .Where(x => x.Distance < DedupThreshold)
            .OrderBy(x => x.Distance)
            .FirstOrDefaultAsync(ct);

        string canonicalName;

        if (canonicalMatch != null)
        {
            canonicalName = canonicalMatch.Skill.Name;
            _logger.LogDebug("Deduplicated: '{Raw}' -> '{Canonical}' (Dist: {Dist:F3})",
                name, canonicalName, canonicalMatch.Distance);

            // Only Roadmap.sh can upgrade an existing skill to is_tech=true.
            // O*NET's /technology_skills endpoint uses a broad "technical skills" definition
            // that includes cognitive skills (e.g. "Critical Thinking", "Mathematics"),
            // so allowing ONET_Skill source to upgrade would corrupt the cognitive taxonomy.
            if (isTech && !canonicalMatch.Skill.IsTech && source == "Roadmap.sh")
            {
                _logger.LogInformation("Upgrading '{Skill}' to is_tech=true (Roadmap.sh)", canonicalName);
                canonicalMatch.Skill.IsTech = true;
                await dbContext.SaveChangesAsync(ct);
                await neo4j.SetSkillIsTechAsync(canonicalName, true);
            }
        }
        else
        {
            var skill = new RefSkill
            {
                Name = name,
                Source = source,
                IsTech = isTech,
                Embedding = embedding
            };
            dbContext.Skills.Add(skill);
            await dbContext.SaveChangesAsync(ct);
            canonicalName = name;
        }

        await neo4j.MergeSkillAsync(canonicalName, source, isTech);
        return canonicalName;
    }

    private async Task LinkSkillToRoleInPostgresAsync(
        ArisDbContext dbContext,
        int roleId,
        string canonicalSkillName,
        CancellationToken ct)
    {
        var skill = await dbContext.Skills
            .FirstOrDefaultAsync(s => s.Name == canonicalSkillName, ct);
        if (skill == null) return;

        // Check local change tracker first — the same canonical skill can appear in multiple
        // lists (Skills, Knowledge, WorkActivities, TechSkills) within one occupation, and
        // SaveChanges hasn't been called yet, so AnyAsync alone would return false twice.
        var alreadyTracked = dbContext.RoleSkills.Local
            .Any(rs => rs.RoleId == roleId && rs.SkillId == skill.Id);
        if (alreadyTracked) return;

        var existsInDb = await dbContext.RoleSkills
            .AnyAsync(rs => rs.RoleId == roleId && rs.SkillId == skill.Id, ct);
        if (!existsInDb)
        {
            dbContext.RoleSkills.Add(new RefRoleSkill
            {
                RoleId = roleId,
                SkillId = skill.Id,
                Importance = 0,
                Level = 0
            });
        }
    }

    private async Task EnsureCleanSlateAsync(
        ArisDbContext dbContext,
        Neo4jIngestionService neo4j,
        CancellationToken ct)
    {
        _logger.LogWarning("!!! CLEARING ALL DICTIONARY DATA !!!");
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE ref_role_skills, ref_skills, ref_roles RESTART IDENTITY CASCADE;", ct);
        _logger.LogInformation("PostgreSQL Dictionary Tables Truncated.");
        await neo4j.ClearDatabaseAsync();
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
            _logger.LogError(ex, "Failed to generate embedding for: {Text}",
                text.Length > 50 ? text[..50] + "..." : text);
            return null;
        }
    }

    // ── --optimize-graph Mode ──────────────────────────────────────────────────

    private async Task RunOptimizeGraphAsync(
        string[] args,
        Neo4jIngestionService neo4jService,
        OntologyEnrichmentService ontologyService,
        CancellationToken stoppingToken)
    {
        int? limit = null;
        var limitIndex = Array.IndexOf(args, "--limit");
        if (limitIndex >= 0 && limitIndex + 1 < args.Length &&
            int.TryParse(args[limitIndex + 1], out var parsedLimit))
            limit = parsedLimit;

        var skipBridges = args.Contains("--skip-bridges");
        var skipDeps = args.Contains("--skip-deps");
        var skipPass1 = args.Contains("--skip-pass1");
        var skipPass2 = args.Contains("--skip-pass2");

        int resumeIndex = 0;
        var resumeIdx = Array.IndexOf(args, "--resume");
        if (resumeIdx >= 0 && resumeIdx + 1 < args.Length &&
            int.TryParse(args[resumeIdx + 1], out var parsedResume))
            resumeIndex = parsedResume;

        if (resumeIndex > 0) _logger.LogInformation("Resuming from index: {ResumeIndex}", resumeIndex);

        int resumeSiblingsIndex = 0;
        var resumeSiblingsIdx = Array.IndexOf(args, "--resume-siblings");
        if (resumeSiblingsIdx >= 0 && resumeSiblingsIdx + 1 < args.Length &&
            int.TryParse(args[resumeSiblingsIdx + 1], out var parsedResumeSiblings))
            resumeSiblingsIndex = parsedResumeSiblings;

        if (resumeSiblingsIndex > 0)
            _logger.LogInformation("Step 2: Resuming from sibling cluster index: {ResumeIndex}", resumeSiblingsIndex);

        int maxClusterSize = 150;
        var maxClusterIdx = Array.IndexOf(args, "--max-cluster");
        if (maxClusterIdx >= 0 && maxClusterIdx + 1 < args.Length &&
            int.TryParse(args[maxClusterIdx + 1], out var parsedMaxCluster))
            maxClusterSize = parsedMaxCluster;

        var processedBridges = new HashSet<string>();
        var processedDeps = new HashSet<string>();

        // Pre-populate processedBridges with edges already in the graph.
        // This prevents re-logging Roadmap.sh native bridges as "New" and
        // skips redundant MergeBridgeRelationshipAsync calls for existing pairs.
        _logger.LogInformation("Loading existing BRIDGE_TO edges into dedup set...");
        var existingBridgeKeys = await neo4jService.GetExistingBridgeKeysAsync();
        foreach (var key in existingBridgeKeys)
            processedBridges.Add(key);
        _logger.LogInformation("Loaded {Count} existing bridge keys. Only new bridges will be logged.", existingBridgeKeys.Count);

        // ── Step 1: Dependency Detection (runs first so SUBSET_OF is established before
        //           bridge passes — gives Pass 2 accurate pruning and avoids the
        //           create-bridge-then-delete-it roundtrip in MergeSubsetRelationshipAsync) ──
        if (!skipDeps)
        {
            _logger.LogInformation("=== Step 1: Dependency Detection ===");
            var roles3 = await neo4jService.GetRolesWithDirectSkillsAsync();
            _logger.LogInformation("Found {Count} roles to analyze in Step 1.", roles3.Count);
            var processedSkillHashes3 = new HashSet<string>();
            var processedCount3 = 0;

            foreach (var role in roles3)
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (limit.HasValue && processedCount3 >= limit.Value) break;

                var techSkills = role.Skills.Where(s => s.Length < 60).OrderBy(s => s).ToList();
                var skillHash = string.Join(",", techSkills);

                if (techSkills.Count <= 1 || !processedSkillHashes3.Add(skillHash)) continue;

                processedCount3++;

                var depsResumeIndex = args.Contains("--resume-deps")
                    ? int.Parse(args[Array.IndexOf(args, "--resume-deps") + 1]) : 0;

                if (processedCount3 < depsResumeIndex)
                {
                    if (processedCount3 % 100 == 0 || processedCount3 == depsResumeIndex - 1)
                        _logger.LogInformation("  [Step 1] Skipping {Current}/{ResumeIndex}...",
                            processedCount3, depsResumeIndex);
                    continue;
                }

                _logger.LogInformation("[Dependency {Current}/{Total}] '{Role}' ({Count} skills)",
                    processedCount3, limit ?? roles3.Count, role.RoleTitle, techSkills.Count);

                var deps = await ontologyService.FindDependenciesAsync(role.RoleTitle, techSkills, stoppingToken);
                foreach (var dep in deps)
                {
                    var key = $"{dep.Child}|{dep.Parent}";
                    if (processedDeps.Add(key))
                    {
                        _logger.LogInformation("  + New Dependency: {Child} -> {Parent}", dep.Child, dep.Parent);
                        await neo4jService.MergeSubsetRelationshipAsync(dep.Child, dep.Parent,
                            "OntologyEnrichment");
                    }
                }
            }
        }

        // ── Steps 2–4: Bridge Detection (runs after deps so sibling pruning is accurate) ──
        if (!skipBridges)
        {
            if (!skipPass1)
            {
                _logger.LogInformation("=== Step 2: Sibling Bridge Detection ===");
                var siblings = await neo4jService.GetSiblingClustersAsync();
                var clusterCount = 0;

                foreach (var cluster in siblings)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (cluster.Siblings.Count <= 1) continue;

                    clusterCount++;

                    if (clusterCount < resumeSiblingsIndex)
                    {
                        if (clusterCount % 100 == 0 || clusterCount == resumeSiblingsIndex - 1)
                            _logger.LogInformation("  [Step 2] Skipping {Current}/{ResumeIndex}...",
                                clusterCount, resumeSiblingsIndex);
                        continue;
                    }

                    if (cluster.Siblings.Count > maxClusterSize)
                    {
                        _logger.LogInformation(
                            "[Sibling Cluster {Current}/{Total}] Skipping '{Parent}' — {Count} siblings exceeds max cluster size ({Max}).",
                            clusterCount, siblings.Count, cluster.Parent, cluster.Siblings.Count, maxClusterSize);
                        continue;
                    }

                    _logger.LogInformation(
                        "[Sibling Cluster {Current}/{Total}] Analyzing '{Parent}' with {Count} siblings",
                        clusterCount, siblings.Count, cluster.Parent, cluster.Siblings.Count);

                    var hierarchies = await neo4jService.GetInternalHierarchiesAsync(cluster.Siblings);
                    var parentNames = hierarchies.Select(h => h.Parent).ToHashSet();
                    var prunedSkills = cluster.Siblings.Where(s => !parentNames.Contains(s)).ToList();

                    if (prunedSkills.Count < 2)
                    {
                        _logger.LogInformation("  - Skipping: Pruning left less than 2 skills.");
                        continue;
                    }

                    var bridges = await ontologyService.FindBridgesAsync(
                        cluster.Parent, prunedSkills, hierarchies, stoppingToken);
                    foreach (var bridge in bridges)
                    {
                        var key = string.Join("|", new[] { bridge.Source, bridge.Target }.OrderBy(s => s));
                        if (processedBridges.Add(key))
                        {
                            _logger.LogInformation("  + New Bridge: {Source} <-> {Target}", bridge.Source, bridge.Target);
                            await neo4jService.MergeBridgeRelationshipAsync(bridge.Source, bridge.Target,
                                "OntologyEnrichment");
                        }
                    }
                }
            }

            if (skipPass2)
            {
                _logger.LogInformation("=== Step 3: Role-Based Bridge Detection === SKIPPED (--skip-pass2)");
            }
            else
            {
                _logger.LogInformation("=== Step 3: Role-Based Bridge Detection ===");
                var roles2 = await neo4jService.GetRolesWithDirectSkillsAsync();
                _logger.LogInformation("Found {Count} roles to analyze in Step 3.", roles2.Count);
                var processedSkillHashes2 = new HashSet<string>();
                var processedCount2 = 0;

                foreach (var role in roles2)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    if (limit.HasValue && processedCount2 >= limit.Value) break;

                    var techSkills = role.Skills.Where(s => s.Length < 60).OrderBy(s => s).ToList();
                    var skillHash = string.Join(",", techSkills);

                    if (techSkills.Count <= 1 || !processedSkillHashes2.Add(skillHash)) continue;

                    processedCount2++;
                    if (processedCount2 < resumeIndex)
                    {
                        if (processedCount2 % 100 == 0 || processedCount2 == resumeIndex - 1)
                            _logger.LogInformation("  [Step 3] Skipping {Current}/{ResumeIndex}...",
                                processedCount2, resumeIndex);
                        continue;
                    }

                    _logger.LogInformation("[Role Bridge {Current}/{Total}] '{Role}' ({Count} skills)",
                        processedCount2, limit ?? roles2.Count, role.RoleTitle, techSkills.Count);

                    var hierarchies2 = await neo4jService.GetInternalHierarchiesAsync(techSkills);
                    var parentNames2 = hierarchies2.Select(h => h.Parent).ToHashSet();
                    var prunedSkills2 = techSkills.Where(s => !parentNames2.Contains(s)).ToList();

                    if (prunedSkills2.Count < 2)
                    {
                        _logger.LogInformation("  - Skipping: Pruning left less than 2 skills.");
                        continue;
                    }

                    var bridges2 = await ontologyService.FindBridgesAsync(
                        role.RoleTitle, prunedSkills2, hierarchies2, stoppingToken);
                    foreach (var bridge in bridges2)
                    {
                        var key = string.Join("|", new[] { bridge.Source, bridge.Target }.OrderBy(s => s));
                        if (processedBridges.Add(key))
                        {
                            _logger.LogInformation("  + New Bridge: {Source} <-> {Target}", bridge.Source, bridge.Target);
                            await neo4jService.MergeBridgeRelationshipAsync(bridge.Source, bridge.Target,
                                "OntologyEnrichment");
                        }
                    }
                }
            }

            _logger.LogInformation("=== Step 4: Domain-Tool Bridge Detection (Pure O*NET Roles) ===");
            var domainRoles = await neo4jService.GetDomainToolRolesAsync();
            _logger.LogInformation("Found {Count} eligible domain-tool roles for Step 4.", domainRoles.Count);

            var domainPromptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Prompts", "OntologyEnrichmentDomainTools.md");

            int domainResumeIndex = 0;
            var domainResumeIdx = Array.IndexOf(args, "--resume-domain");
            if (domainResumeIdx >= 0 && domainResumeIdx + 1 < args.Length &&
                int.TryParse(args[domainResumeIdx + 1], out var parsedDomainResume))
                domainResumeIndex = parsedDomainResume;

            if (domainResumeIndex > 0)
                _logger.LogInformation("Step 4: Resuming from index: {ResumeIndex}", domainResumeIndex);

            var processedDomainHashes = new HashSet<string>();
            var processedDomainCount = 0;

            foreach (var role in domainRoles)
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (limit.HasValue && processedDomainCount >= limit.Value) break;

                var toolList = role.Skills.OrderBy(s => s).ToList();
                var skillHash = string.Join(",", toolList);

                if (toolList.Count < 2 || !processedDomainHashes.Add(skillHash)) continue;

                processedDomainCount++;

                if (processedDomainCount < domainResumeIndex)
                {
                    if (processedDomainCount % 100 == 0 || processedDomainCount == domainResumeIndex - 1)
                        _logger.LogInformation("  [Step 4] Skipping {Current}/{ResumeIndex}...",
                            processedDomainCount, domainResumeIndex);
                    continue;
                }

                _logger.LogInformation("[Domain Bridge {Current}/{Total}] '{Role}' ({Count} tools)",
                    processedDomainCount, limit ?? domainRoles.Count, role.RoleTitle, toolList.Count);

                var domainBridges = await ontologyService.FindBridgesAsync(
                    role.RoleTitle, toolList, [], stoppingToken, domainPromptPath);

                foreach (var bridge in domainBridges)
                {
                    var key = string.Join("|", new[] { bridge.Source, bridge.Target }.OrderBy(s => s));
                    if (processedBridges.Add(key))
                    {
                        _logger.LogInformation("  + New Domain Bridge: {Source} <-> {Target}",
                            bridge.Source, bridge.Target);
                        await neo4jService.MergeBridgeRelationshipAsync(bridge.Source, bridge.Target,
                            "OntologyEnrichment");
                    }
                }
            }
        }
    }
}
