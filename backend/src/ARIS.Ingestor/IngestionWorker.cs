using ARIS.Ingestor.Services;
using ARIS.Shared.Entities;
using ARIS.Shared.Models.Ingestion.Onet;
using ARIS.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using Pgvector.EntityFrameworkCore;
using System.Text.Json;

using PgVectorType = Pgvector.Vector;

namespace ARIS.Ingestor;

public class IngestionWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<IngestionWorker> _logger;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingService;

    // Only "subtopic" nodes contain real tool/framework names (e.g. "Django", "pytest", "Docker").
    // "topic" nodes are section headings ("Learn a Framework") — kept as hierarchy anchors only,
    // not ingested as Skill nodes. All layout nodes (vertical, section, button, etc.) are ignored.
    private static readonly HashSet<string> ValidRoadmapNodeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "topic", "subtopic" };

    // Only these node types produce an actual Skill node in the graph.
    private static readonly HashSet<string> SkillNodeTypes =
        new(StringComparer.OrdinalIgnoreCase) { "subtopic" };

    private static readonly string[] RoleRoadmapSlugs =
    [
        "frontend", "backend", "full-stack", "devops", "devsecops",
        "android", "ios", "game-developer", "server-side-game-developer",
        "ai-engineer", "ai-data-scientist", "ai-agents", "ai-red-teaming",
        "machine-learning", "mlops", "data-analyst", "data-engineer", "bi-analyst",
        "qa", "cyber-security",
        "software-architect", "ux-design", "technical-writer",
        "product-manager", "engineering-manager"
    ];

    // Manual O*NET code mappings for role slugs where embedding search is unreliable.
    // Codes verified against O*NET 29.0. Embedding fallback handles anything not listed here.
    private static readonly Dictionary<string, string[]> SlugToOnetCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Web / App Development
            ["frontend"]                    = ["15-1254.00"],  // Web Developers
            ["backend"]                     = ["15-1252.00"],  // Software Developers
            ["full-stack"]                  = ["15-1254.00"],  // Web Developers
            ["android"]                     = ["15-1252.00"],  // Software Developers
            ["ios"]                         = ["15-1252.00"],  // Software Developers

            // Infrastructure / Operations
            ["devops"]                      = ["15-1244.00"],  // Network and Computer Systems Administrators
            ["devsecops"]                   = ["15-1212.00"],  // Information Security Analysts
            ["mlops"]                       = ["15-1244.00"],  // Network and Computer Systems Administrators

            // AI / Data
            ["ai-engineer"]                 = ["15-2051.00"],  // Data Scientists
            ["ai-data-scientist"]           = ["15-2051.00"],  // Data Scientists
            ["ai-agents"]                   = ["15-2051.00"],  // Data Scientists
            ["ai-red-teaming"]              = ["15-1212.00"],  // Information Security Analysts
            ["machine-learning"]            = ["15-2051.00"],  // Data Scientists
            ["data-analyst"]                = ["15-2051.01"],  // Business Intelligence Analysts
            ["data-engineer"]               = ["15-1243.00"],  // Database Architects
            ["bi-analyst"]                  = ["15-2051.01"],  // Business Intelligence Analysts

            // Quality / Security
            ["qa"]                          = ["15-1253.00"],  // Software Quality Assurance Analysts and Testers
            ["cyber-security"]              = ["15-1212.00"],  // Information Security Analysts

            // Architecture / Design
            ["software-architect"]          = ["15-1252.00"],  // Software Developers
            ["ux-design"]                   = ["15-1255.00"],  // Web and Digital Interface Designers

            // Games
            ["game-developer"]              = ["15-1252.00"],  // Software Developers
            ["server-side-game-developer"]  = ["15-1252.00"],  // Software Developers

            // Management / Other
            ["product-manager"]             = ["11-3021.00"],  // Computer and Information Systems Managers
            ["engineering-manager"]         = ["11-3021.00"],  // Computer and Information Systems Managers
            ["technical-writer"]            = ["27-3042.00"],  // Technical Writers

            // blockchain and devrel intentionally omitted — no reliable O*NET analog;
            // embedding fallback will attempt a match and skip if nothing is close enough.
        };

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

        // Special Modes

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


        await dbContext.Database.EnsureCreatedAsync(stoppingToken);
        await neo4jService.EnsureIndicesAsync();

        var onetOnly = args.Contains("--onet-only");
        var roadmapOnly = args.Contains("--roadmap-only");

        if (!roadmapOnly)
        {
            if (args.Contains("--fresh"))
            {
                _logger.LogWarning(">>> FRESH MODE: Clearing all data <<<");
                await EnsureCleanSlateAsync(dbContext, neo4jService, stoppingToken);
            }

            _logger.LogInformation("=== Phase 1: O*NET Ingestion ===");
            await IngestOnetAsync(dbContext, onetService, neo4jService, stoppingToken);

            if (onetOnly)
            {
                _logger.LogInformation("O*NET-only run complete. Inspect the database before running --roadmap-only.");
                _hostApplicationLifetime.StopApplication();
                return;
            }
        }

        _logger.LogInformation("=== Phase 2: Building Slug-Role Map ===");
        var slugRoleMap = await BuildSlugRoleMapAsync(dbContext, roadmapService, RoleRoadmapSlugs, stoppingToken);

        var csvRows = new List<string[]>();

        _logger.LogInformation("=== Phase 3: Role Roadmap Ingestion ===");
        foreach (var slug in RoleRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Processing Role Roadmap: {Slug}", slug);
            var matchedRoles = slugRoleMap.GetValueOrDefault(slug) ?? [];
            await IngestRoadmapAsync(dbContext, roadmapService, neo4jService, slug,
                isRoleRoadmap: true, matchedRoles, csvRows, stoppingToken);
        }

        _logger.LogInformation("=== Phase 4: Skill Roadmap Ingestion ===");
        foreach (var slug in SkillRoadmapSlugs)
        {
            if (stoppingToken.IsCancellationRequested) break;
            _logger.LogInformation("Processing Skill Roadmap: {Slug}", slug);
            await IngestRoadmapAsync(dbContext, roadmapService, neo4jService, slug,
                isRoleRoadmap: false, [], csvRows, stoppingToken);
        }

        await WriteRoadmapCsvAsync(csvRows);

        _logger.LogInformation("=== Phase 5: BERT Bridge Generation ===");
        _logger.LogInformation("  pip install -r Development/scripts/requirements_bert.txt");
        _logger.LogInformation("  python Development/scripts/generate_bert_bridges.py");

        _logger.LogInformation("Ingestion Complete.");
        _hostApplicationLifetime.StopApplication();
    }


    private async Task IngestOnetAsync(
        ArisDbContext dbContext,
        OnetService onetService,
        Neo4jIngestionService neo4j,
        CancellationToken ct)
    {
        _logger.LogInformation("=== Phase 1a: Soft Skill Taxonomy ===");
        await IngestOnetSkillTaxonomiesAsync(dbContext, onetService, neo4j, ct);

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

            // Technology skills (specific software / tools) → IsTech = true
            // Lateral similarity between tech skills is handled by the BERT post-ingestion script
            // (generate_bert_bridges.py), which creates IS_SIMILAR_TO edges based on semantic
            // similarity of skill names and descriptions. ONET_Category BRIDGE_TO was removed
            // because O*NET category groupings are too coarse — tools like "Excel" co-appear
            // with domain-specific software across dozens of categories, creating spurious hops.
            foreach (var techSkillName in details.TechnologySkills)
            {
                var canon = await DeduplicateOrCreateSkillAsync(
                    dbContext, neo4j, techSkillName, "ONET_Skill", isTech: true, ct);
                await neo4j.MergeRoleSkillRelationshipAsync(details.Code, canon);
                await LinkSkillToRoleInPostgresAsync(dbContext, existingRole.Id, canon, ct);
            }

            // Job Zone
            var jobZone = await onetService.GetOccupationJobZoneAsync(occ.Code, ct);
            if (jobZone.HasValue)
            {
                var zoneTitle = $"Job Zone {jobZone.Value}";
                await neo4j.UpsertJobZoneNodeAsync(jobZone.Value, zoneTitle);
                await neo4j.LinkRoleToJobZoneAsync(details.Code, jobZone.Value);
                existingRole.JobZone = jobZone.Value;
            }

            // Knowledge domains
            var knowledgeItems = await onetService.GetOccupationKnowledgeAsync(occ.Code, ct);
            foreach (var k in knowledgeItems)
            {
                await neo4j.UpsertKnowledgeNodeAsync(k.Id, k.Name, k.Description);
                await neo4j.LinkRoleToKnowledgeAsync(details.Code, k.Id, k.Importance);
                await UpsertKnowledgeInPostgresAsync(dbContext, existingRole.Id, k, ct);
            }

            // Abilities
            var abilities = await onetService.GetOccupationAbilitiesAsync(occ.Code, ct);
            foreach (var a in abilities)
            {
                await neo4j.UpsertAbilityNodeAsync(a.Id, a.Name, a.Description);
                await neo4j.LinkRoleToAbilityAsync(details.Code, a.Id, a.Importance);
                await UpsertAbilityInPostgresAsync(dbContext, existingRole.Id, a, ct);
            }

            // Tasks (Neo4j only — task descriptions are sentences, not searchable by name in Postgres)
            var tasks = await onetService.GetOccupationTasksAsync(occ.Code, ct);
            foreach (var t in tasks)
            {
                await neo4j.UpsertTaskNodeAsync(t.Id, t.Statement, t.Importance);
                await neo4j.LinkRoleToTaskAsync(details.Code, t.Id, t.Importance);
            }

            // Work Activities (Neo4j only)
            var workActivities = await onetService.GetOccupationWorkActivitiesAsync(occ.Code, ct);
            foreach (var w in workActivities)
            {
                await neo4j.UpsertWorkActivityNodeAsync(w.Id, w.Name, w.Description);
                await neo4j.LinkRoleToWorkActivityAsync(details.Code, w.Id, w.Importance);
            }

            await dbContext.SaveChangesAsync(ct);
            await Task.Delay(200, ct);
        }

        _logger.LogInformation("O*NET Ingestion Complete: {Count} occupations processed.", i);
    }

    // Build Slug → Role Map

    private async Task<Dictionary<string, List<string>>> BuildSlugRoleMapAsync(
        ArisDbContext dbContext,
        RoadmapService roadmapService,
        string[] slugs,
        CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Pre-load all role codes that exist in the DB so we can validate manual entries
        var existingCodes = await dbContext.Roles
            .Where(r => r.OnetCode != null)
            .Select(r => r.OnetCode!)
            .ToHashSetAsync(ct);

        foreach (var slug in slugs)
        {
            if (ct.IsCancellationRequested) break;
            if (SlugToOnetCodes.TryGetValue(slug, out var manualCodes))
            {
                var validCodes = manualCodes.Where(c => existingCodes.Contains(c)).ToList();
                map[slug] = validCodes;
                _logger.LogInformation("Slug '{Slug}' → manual map → {Count} O*NET roles: {Roles}",
                    slug, validCodes.Count, string.Join(", ", validCodes));
                continue;
            }

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
            _logger.LogInformation("Slug '{Slug}' ({Title}) → embedding fallback → {Count} O*NET roles: {Roles}",
                slug, title, map[slug].Count,
                string.Join(", ", matches.Select(m => $"{m.Title} ({m.OnetCode})")));

            await Task.Delay(100, ct);
        }

        return map;
    }


    private async Task IngestOnetSkillTaxonomiesAsync(
        ArisDbContext dbContext,
        OnetService onetService,
        Neo4jIngestionService neo4j,
        CancellationToken ct)
    {
        var basicSkills = await onetService.GetSkillTaxonomyAsync("online/onet_data/skills_basic/", ct);
        var cfSkills = await onetService.GetSkillTaxonomyAsync("online/onet_data/skills_cf/", ct);
        var allRoots = basicSkills.Concat(cfSkills).ToList();

        _logger.LogInformation("Fetched {Count} taxonomy root categories from O*NET.", allRoots.Count);

        foreach (var root in allRoots)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessTaxonomyNodeAsync(dbContext, neo4j, root, parentName: null, ct);
        }

        _logger.LogInformation("Soft skill taxonomy ingestion complete.");
    }

    private async Task ProcessTaxonomyNodeAsync(
        ArisDbContext dbContext,
        Neo4jIngestionService neo4j,
        SkillTaxonomyNode node,
        string? parentName,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(node.Name)) return;

        await DeduplicateOrCreateSkillAsync(dbContext, neo4j, node.Name, "ONET_Taxonomy", isTech: false, ct);

        if (parentName != null)
            await neo4j.MergeSubsetRelationshipAsync(node.Name, parentName, "ONET_Taxonomy");

        if (node.Child == null) return;

        foreach (var child in node.Child)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessTaxonomyNodeAsync(dbContext, neo4j, child, node.Name, ct);
        }
    }


    private async Task IngestRoadmapAsync(
        ArisDbContext dbContext,
        RoadmapService roadmapService,
        Neo4jIngestionService neo4j,
        string slug,
        bool isRoleRoadmap,
        List<string> matchedRoleCodes,
        List<string[]> csvRows,
        CancellationToken ct)
    {
        var roadmap = await roadmapService.GetRoadmapAsync(slug, ct);
        if (roadmap?.Nodes == null)
        {
            _logger.LogWarning("Roadmap '{Slug}' returned no nodes — skipping.", slug);
            return;
        }

        // Build nodeId → canonical skill name map
        var nodeIdToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in roadmap.Nodes)
        {
            if (!ValidRoadmapNodeTypes.Contains(node.Type ?? "")) continue;
            var label = node.Data?.Label?.Trim();
            if (string.IsNullOrWhiteSpace(label) || node.Id == null) continue;

            // topic nodes are hierarchy anchors for edge processing — track their ID but
            // do NOT create a Skill node (topic labels are section headings, not tool names).
            if (!SkillNodeTypes.Contains(node.Type ?? ""))
            {
                nodeIdToCanonical[node.Id] = label; // used as edge anchor only
                continue;
            }

            // Deterministic filter — removes navigation labels, instructional text,
            // framework hooks, and comparison phrases. No LLM required.
            if (IsRoadmapJunkLabel(label)) continue;

            var canon = await DeduplicateOrCreateSkillAsync(
                dbContext, neo4j, label, "Roadmap.sh", isTech: true, ct);
            if (!string.IsNullOrEmpty(canon))
                nodeIdToCanonical[node.Id] = canon;
        }

        _logger.LogInformation("Roadmap '{Slug}': {Count} valid skills processed.", slug, nodeIdToCanonical.Count);

        // Process edges — only subtopic→subtopic edges carry real skill graph semantics.
        // topic→subtopic edges (the dominant pattern, 1,669 total) encode category membership
        // and are handled below by linking all subtopics to the root skill or role.
        // subtopic→subtopic dashed edges (80 total) encode sub-variants, e.g.
        // "Policies → Resource-based" or "System Prompting → Role & Behavior" → SUBSET_OF.
        var skillNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in roadmap.Nodes ?? [])
        {
            if (SkillNodeTypes.Contains(node.Type ?? "") && node.Id != null
                && nodeIdToCanonical.ContainsKey(node.Id))
                skillNodeIds.Add(node.Id);
        }

        if (roadmap.Edges != null)
        {
            foreach (var edge in roadmap.Edges)
            {
                if (edge.Source == null || edge.Target == null) continue;
                // Only process edges where BOTH endpoints are actual skill (subtopic) nodes
                if (!skillNodeIds.Contains(edge.Source) || !skillNodeIds.Contains(edge.Target)) continue;

                var parentCanon = nodeIdToCanonical[edge.Source];
                var childCanon = nodeIdToCanonical[edge.Target];
                if (string.Equals(parentCanon, childCanon, StringComparison.OrdinalIgnoreCase)) continue;

                // subtopic→subtopic edges are sub-variants → child SUBSET_OF parent
                await neo4j.MergeSubsetRelationshipAsync(childCanon, parentCanon, "Roadmap.sh");
                csvRows.Add([slug, "Skill", parentCanon, childCanon, "SUBSET_OF"]);
            }
        }

        if (!isRoleRoadmap)
        {
            // Skill roadmap: all subtopics are SUBSET_OF the root skill (e.g. "React", "Python")
            var rootSkillName = roadmap.Title?.Card ?? slug.Replace("-", " ");
            await DeduplicateOrCreateSkillAsync(dbContext, neo4j, rootSkillName, "Roadmap.sh", isTech: true, ct);

            foreach (var (nodeId, canonName) in nodeIdToCanonical)
            {
                if (!skillNodeIds.Contains(nodeId)) continue; // skip topic anchors
                if (string.Equals(canonName, rootSkillName, StringComparison.OrdinalIgnoreCase)) continue;
                await neo4j.MergeSubsetRelationshipAsync(canonName, rootSkillName, "Roadmap.sh");
                csvRows.Add([slug, "Skill", rootSkillName, canonName, "SUBSET_OF"]);
            }
        }
        else
        {
            if (matchedRoleCodes.Count == 0)
            {
                _logger.LogWarning(
                    "Roadmap '{Slug}' has no matched O*NET roles — skipping skill ingestion to avoid orphaned nodes.", slug);
                return;
            }

            var pgRoles = await dbContext.Roles
                .Where(r => r.OnetCode != null && matchedRoleCodes.Contains(r.OnetCode))
                .ToListAsync(ct);

            foreach (var (nodeId, canonName) in nodeIdToCanonical)
            {
                if (!skillNodeIds.Contains(nodeId)) continue; // skip topic anchors

                foreach (var roleCode in matchedRoleCodes)
                {
                    // Neo4j REQUIRES edge
                    await neo4j.MergeRoleSkillRelationshipAsync(roleCode, canonName);
                }

                foreach (var pgRole in pgRoles)
                {
                    await LinkSkillToRoleInPostgresAsync(dbContext, pgRole.Id, canonName, ct);
                    csvRows.Add([slug, "Occupation", pgRole.Title, canonName, "REQUIRES"]);
                }
            }

            await dbContext.SaveChangesAsync(ct);
        }

        await Task.Delay(500, ct);
    }

    // Roadmap Preprocessing 
    /// <summary>
    /// Runs the LLM against a single roadmap's node labels and saves the approved
    /// skill list to Roadmaps/preprocessed/{slug}.json. Skips if file already exists.
    /// </summary>
    // CSV Export

    private async Task WriteRoadmapCsvAsync(List<string[]> rows)
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "roadmap_graph.csv");
        await using var writer = new StreamWriter(outputPath, append: false, encoding: System.Text.Encoding.UTF8);
        await writer.WriteLineAsync("Roadmap,RoadmapType,Parent,Child,Relationship");
        foreach (var row in rows)
            await writer.WriteLineAsync(string.Join(",", row.Select(CsvEscape)));
        _logger.LogInformation("Roadmap graph CSV written: {Path} ({Count} rows)", outputPath, rows.Count);
    }

    private static string CsvEscape(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
    }

    //Shared Helpers

    /// <summary>
    /// Returns true if a Roadmap.sh subtopic label should be excluded from ingestion.
    /// Rules (all deterministic, no LLM):
    ///   1. Contains '?'                    — question/navigation labels ("What is HTTP?")
    ///   2. More than 6 words               — long instructional phrases
    ///   3. Starts with "use" + uppercase   — framework hooks (useState, useEffect, useCallback…)
    ///   4. Contains " vs "                 — comparison phrases ("Props vs State")
    ///   5. Starts with a known gerund verb — instructional steps ("Installing Git", "Creating Modules")
    /// </summary>
    private static bool IsRoadmapJunkLabel(string label)
    {
        if (label.Contains('?')) return true;
        if (label.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6) return true;

        // Framework hooks: useState, useEffect, useCallback, useRef, useMemo, etc.
        // Matches "use" followed immediately by an uppercase letter.
        // Does NOT match: pytest, pyTorch, numpy, useragent (lowercase next char).
        if (label.Length > 3 && label.StartsWith("use") &&
            char.IsUpper(label[3])) return true;

        if (label.Contains(" vs ", StringComparison.OrdinalIgnoreCase)) return true;

        // Instructional gerund prefixes — covers "Installing X", "Creating Y", "Running Z", etc.
        ReadOnlySpan<string> gerunds =
        [
            "Creating ", "Getting ", "Installing ", "Using ", "Building ", "Making ",
            "Learning ", "Understanding ", "Setting ", "Running ", "Writing ", "Connecting ",
            "Deploying ", "Configuring ", "Implementing ", "Working ", "Adding ", "Handling ",
        ];
        foreach (var g in gerunds)
            if (label.StartsWith(g, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    private async Task<string> DeduplicateOrCreateSkillAsync(
        ArisDbContext dbContext,
        Neo4jIngestionService neo4j,
        string name,
        string source,
        bool isTech,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var exactMatch = await dbContext.Skills
            .FirstOrDefaultAsync(s => s.Name == name, ct);

        string canonicalName;

        if (exactMatch != null)
        {
            canonicalName = exactMatch.Name;

            if (isTech && !exactMatch.IsTech && source == "Roadmap.sh"
                && exactMatch.Source != "ONET_Taxonomy")
            {
                _logger.LogInformation("Upgrading '{Skill}' to is_tech=true (Roadmap.sh)", canonicalName);
                exactMatch.IsTech = true;
                await dbContext.SaveChangesAsync(ct);
                await neo4j.SetSkillIsTechAsync(canonicalName, true);
            }
        }
        else
        {
            var embedding = await GenerateEmbeddingAsync(name);
            if (embedding == null) return name;

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

    private async Task UpsertKnowledgeInPostgresAsync(
        ArisDbContext dbContext, int roleId, OnetElement element, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(element.Id) || string.IsNullOrWhiteSpace(element.Name)) return;

        var existing = await dbContext.RefKnowledge
            .FirstOrDefaultAsync(k => k.OnetId == element.Id, ct);

        if (existing == null)
        {
            var embedding = await GenerateEmbeddingAsync($"{element.Name}: {element.Description}");
            existing = new RefKnowledge
            {
                OnetId = element.Id,
                Name = element.Name,
                Description = element.Description,
                Embedding = embedding
            };
            dbContext.RefKnowledge.Add(existing);
            await dbContext.SaveChangesAsync(ct);
        }

        var alreadyLinked = dbContext.RefRoleKnowledge.Local
            .Any(rk => rk.RoleId == roleId && rk.KnowledgeId == existing.Id)
            || await dbContext.RefRoleKnowledge
                .AnyAsync(rk => rk.RoleId == roleId && rk.KnowledgeId == existing.Id, ct);

        if (!alreadyLinked)
        {
            dbContext.RefRoleKnowledge.Add(new RefRoleKnowledge
            {
                RoleId = roleId,
                KnowledgeId = existing.Id,
                Importance = element.Importance
            });
        }
    }

    private async Task UpsertAbilityInPostgresAsync(
        ArisDbContext dbContext, int roleId, OnetElement element, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(element.Id) || string.IsNullOrWhiteSpace(element.Name)) return;

        var existing = await dbContext.RefAbility
            .FirstOrDefaultAsync(a => a.OnetId == element.Id, ct);

        if (existing == null)
        {
            var embedding = await GenerateEmbeddingAsync($"{element.Name}: {element.Description}");
            existing = new RefAbility
            {
                OnetId = element.Id,
                Name = element.Name,
                Description = element.Description,
                Embedding = embedding
            };
            dbContext.RefAbility.Add(existing);
            await dbContext.SaveChangesAsync(ct);
        }

        var alreadyLinked = dbContext.RefRoleAbility.Local
            .Any(ra => ra.RoleId == roleId && ra.AbilityId == existing.Id)
            || await dbContext.RefRoleAbility
                .AnyAsync(ra => ra.RoleId == roleId && ra.AbilityId == existing.Id, ct);

        if (!alreadyLinked)
        {
            dbContext.RefRoleAbility.Add(new RefRoleAbility
            {
                RoleId = roleId,
                AbilityId = existing.Id,
                Importance = element.Importance
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
            "TRUNCATE TABLE ref_role_knowledge, ref_role_ability, ref_role_skills, " +
            "ref_knowledge, ref_ability, ref_skills, ref_roles RESTART IDENTITY CASCADE;", ct);
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

    private static readonly string CheckpointPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aris_ingestor_checkpoint.json");

    private sealed record IngestionCheckpoint(int SiblingsIndex, string Timestamp);

    private static async Task WriteCheckpointAsync(int siblingsIdx)
    {
        var cp = new IngestionCheckpoint(siblingsIdx, DateTimeOffset.UtcNow.ToString("O"));
        await File.WriteAllTextAsync(CheckpointPath,
            JsonSerializer.Serialize(cp, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<IngestionCheckpoint?> ReadCheckpointAsync()
    {
        if (!File.Exists(CheckpointPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<IngestionCheckpoint>(
                await File.ReadAllTextAsync(CheckpointPath));
        }
        catch { return null; }
    }

    private static void DeleteCheckpoint()
    {
        if (File.Exists(CheckpointPath)) File.Delete(CheckpointPath);
    }

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

        int resumeSiblingsIndex = 0;
        var resumeSiblingsIdx = Array.IndexOf(args, "--resume-siblings");
        if (resumeSiblingsIdx >= 0 && resumeSiblingsIdx + 1 < args.Length &&
            int.TryParse(args[resumeSiblingsIdx + 1], out var parsedResumeSiblings))
            resumeSiblingsIndex = parsedResumeSiblings;

        var checkpoint = await ReadCheckpointAsync();
        if (checkpoint != null)
        {
            _logger.LogInformation("Auto-resuming from checkpoint ({Timestamp})", checkpoint.Timestamp);
            if (checkpoint.SiblingsIndex > 0)
            {
                resumeSiblingsIndex = checkpoint.SiblingsIndex;
                _logger.LogInformation("  Checkpoint overriding siblings index to {Index}", checkpoint.SiblingsIndex);
            }
        }

        int maxClusterSize = 150;
        var maxClusterIdx = Array.IndexOf(args, "--max-cluster");
        if (maxClusterIdx >= 0 && maxClusterIdx + 1 < args.Length &&
            int.TryParse(args[maxClusterIdx + 1], out var parsedMaxCluster))
            maxClusterSize = parsedMaxCluster;

        var processedBridges = new HashSet<string>();
        _logger.LogInformation("Loading existing BRIDGE_TO edges into dedup set...");
        var existingBridgeKeys = await neo4jService.GetExistingBridgeKeysAsync();
        foreach (var key in existingBridgeKeys)
            processedBridges.Add(key);
        _logger.LogInformation("Loaded {Count} existing bridge keys.", existingBridgeKeys.Count);

        if (!skipBridges)
        {
            _logger.LogInformation("=== Phase 5: Roadmap.sh Sibling Bridge Detection ===");
            var siblings = await neo4jService.GetRoadmapSiblingClustersAsync();
            _logger.LogInformation("Found {Count} Roadmap.sh sibling clusters.", siblings.Count);
            var clusterCount = 0;

            foreach (var cluster in siblings)
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (cluster.Siblings.Count <= 1) continue;

                clusterCount++;

                if (clusterCount < resumeSiblingsIndex)
                {
                    if (clusterCount % 100 == 0 || clusterCount == resumeSiblingsIndex - 1)
                        _logger.LogInformation("  Skipping {Current}/{ResumeIndex}...",
                            clusterCount, resumeSiblingsIndex);
                    continue;
                }

                if (cluster.Siblings.Count > maxClusterSize)
                {
                    _logger.LogInformation(
                        "[Sibling Cluster {Current}/{Total}] Skipping '{Parent}' — {Count} siblings exceeds max ({Max}).",
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

                await WriteCheckpointAsync(clusterCount + 1);
            }
        }

        DeleteCheckpoint();
        _logger.LogInformation("Ontology enrichment complete. Checkpoint cleared.");
    }
}
