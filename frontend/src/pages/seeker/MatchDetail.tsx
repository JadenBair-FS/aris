import { useState } from 'react';
import { useParams, useNavigate } from 'react-router';
import { useQuery, useMutation } from '@tanstack/react-query';
import { matchApi } from '@/api/match';
import { jobApi } from '@/api/job';
import { resumeApi } from '@/api/resume';
import { TierBreakdown } from '@/components/TierBreakdown';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Badge } from '@/components/ui/badge';
import { ArrowLeft, Loader2, ScanSearch, Info } from 'lucide-react';
import type { MatchAnalysisResult, TailoredBullet } from '@/types/api';

// ── Left panel: job details ───────────────────────────────────────────────────

function JobPanel({ jobId }: { jobId: string }) {
    const { data: job, isLoading, isError } = useQuery({
        queryKey: ['job', jobId],
        queryFn: () => jobApi.getJob(jobId),
    });

    if (isLoading) {
        return (
            <div className="space-y-4">
                <Skeleton className="h-8 w-48" />
                <Skeleton className="h-40 w-full" />
                <Skeleton className="h-32 w-full" />
            </div>
        );
    }

    if (isError || !job) {
        return <p className="text-sm text-red-500">Failed to load job details.</p>;
    }

    const signal = job.cleanSignal;
    const primaryRole = signal?.target_roles?.[0]?.title ?? 'Job Posting';
    const essential = signal?.required_skills?.filter(s => s.importance.toLowerCase() === 'essential') ?? [];
    const preferred = signal?.required_skills?.filter(s => s.importance.toLowerCase() !== 'essential') ?? [];

    return (
        <div className="space-y-3">
            {/* Title */}
            <Card>
                <CardContent className="px-4 py-3">
                    <h2 className="text-base font-semibold text-slate-900">{primaryRole}</h2>
                    <p className="text-xs text-slate-400 font-mono mt-0.5">{job.id.slice(0, 8)}</p>
                    {signal?.target_roles && signal.target_roles.length > 1 && (
                        <div className="flex flex-wrap gap-1.5 mt-2">
                            {signal.target_roles.slice(1).map((r, i) => (
                                <Badge key={i} variant="secondary" className="text-xs">{r.title}</Badge>
                            ))}
                        </div>
                    )}
                </CardContent>
            </Card>

            {/* Responsibilities */}
            {signal?.responsibilities && signal.responsibilities.length > 0 && (
                <Card>
                    <CardContent className="px-4 py-3">
                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2">Description</p>
                        <ul className="space-y-1.5">
                            {signal.responsibilities.map((r, i) => (
                                <li key={i} className="flex gap-2 text-sm text-slate-700">
                                    <span className="text-slate-300 shrink-0 mt-0.5">·</span>
                                    {r}
                                </li>
                            ))}
                        </ul>
                    </CardContent>
                </Card>
            )}

            {/* Required skills */}
            {signal && (essential.length > 0 || preferred.length > 0) && (
                <Card>
                    <CardContent className="px-4 py-3 space-y-3">
                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Skills</p>
                        {essential.length > 0 && (
                            <div>
                                <p className="text-xs text-slate-400 mb-1.5">Essential</p>
                                <div className="flex flex-wrap gap-1.5">
                                    {essential.map(s => (
                                        <Badge key={s.name} className="text-xs bg-slate-900 text-white hover:bg-slate-800">
                                            {s.name}
                                            {s.years_of_experience > 0 && <span className="ml-1 opacity-70">· {s.years_of_experience}yr</span>}
                                        </Badge>
                                    ))}
                                </div>
                            </div>
                        )}
                        {preferred.length > 0 && (
                            <div>
                                <p className="text-xs text-slate-400 mb-1.5">Preferred</p>
                                <div className="flex flex-wrap gap-1.5">
                                    {preferred.map(s => (
                                        <Badge key={s.name} variant="secondary" className="text-xs">
                                            {s.name}
                                            {s.years_of_experience > 0 && <span className="ml-1 opacity-60">· {s.years_of_experience}yr</span>}
                                        </Badge>
                                    ))}
                                </div>
                            </div>
                        )}
                    </CardContent>
                </Card>
            )}
        </div>
    );
}

// ── Right panel: analysis ─────────────────────────────────────────────────────

function AnalysisPanel({
    profileId,
    jobId,
}: {
    profileId: string;
    jobId: string;
}) {
    const [analysis, setAnalysis] = useState<MatchAnalysisResult | null>(null);
    const [summary, setSummary] = useState<{ text: string; score: number } | null>(null);
    const [tailoredBullets, setTailoredBullets] = useState<TailoredBullet[] | null>(null);

    const analyzeMutation = useMutation({
        mutationFn: () => matchApi.analyze(profileId, jobId),
        onSuccess: (data) => setAnalysis(data),
    });

    const summaryMutation = useMutation({
        mutationFn: () => matchApi.getSummary(profileId, jobId),
        onSuccess: (data) => setSummary({ text: data.summary, score: data.groundingScore }),
    });

    const tailorMutation = useMutation({
        mutationFn: () => resumeApi.tailor(profileId, jobId),
        onSuccess: (data) => setTailoredBullets(data),
    });

    // Pre-analysis: prompt card
    if (!analysis) {
        return (
            <div className="flex flex-col items-center justify-center min-h-[360px] rounded-xl border-2 border-dashed border-slate-200 bg-white p-10 text-center">
                <ScanSearch className="h-10 w-10 text-slate-300 mb-4" />
                <h3 className="text-base font-semibold text-slate-800 mb-1">Ready to analyze</h3>
                <p className="text-sm text-slate-400 mb-6 max-w-xs">
                    Run a full knowledge-graph analysis to see how your skills map against this role across all five tiers.
                </p>
                <Button
                    onClick={() => analyzeMutation.mutate()}
                    disabled={analyzeMutation.isPending}
                    className="bg-slate-900 hover:bg-slate-800 text-white px-6"
                    size="lg"
                >
                    {analyzeMutation.isPending
                        ? <><Loader2 className="mr-2 h-4 w-4 animate-spin" /> Analyzing...</>
                        : 'Analyze My Profile'}
                </Button>
                {analyzeMutation.isError && (
                    <p className="text-sm text-red-500 mt-4">Analysis failed. Please try again.</p>
                )}
            </div>
        );
    }

    // Scores summary row
    const arisScore = analysis.arisScore;
    const scoreBadgeClass =
        arisScore >= 0.75 ? 'bg-green-50 text-green-700 border-green-200' :
        arisScore >= 0.60 ? 'bg-yellow-50 text-yellow-700 border-yellow-200' :
        'bg-slate-100 text-slate-600 border-slate-200';

    return (
        <div className="space-y-5">
            {/* Score row */}
            <Card>
                <CardContent className="px-4 py-3 flex items-center justify-between gap-4">
                    <div>
                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-1">ARIS Score</p>
                        <div className="flex items-center gap-2">
                            <span className="text-4xl font-bold text-slate-900">{arisScore.toFixed(2)}</span>
                            <span className={`text-xs font-medium px-2 py-0.5 rounded-full border ${scoreBadgeClass}`}>
                                {arisScore >= 0.75 ? 'Strong fit' : arisScore >= 0.60 ? 'Moderate fit' : 'Partial fit'}
                            </span>
                        </div>
                    </div>
                    <div className="text-right">
                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-1">Embedding Match</p>
                        <span className="text-2xl font-semibold text-slate-500">{analysis.vectorSimilarity.toFixed(2)}</span>
                    </div>
                    <div className="group relative self-start mt-1">
                        <Info className="h-4 w-4 text-slate-300 cursor-help" />
                        <div className="absolute right-0 top-5 hidden group-hover:block w-56 bg-white text-slate-600 text-xs rounded-lg p-2.5 shadow-lg z-10 border border-slate-100">
                            ArisScore = 0.55 × VectorSimilarity + 0.45 × GraphCoverageScore
                        </div>
                    </div>
                </CardContent>
            </Card>

            {/* Five-tier breakdown — always shows all tiers */}
            <div>
                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-3">Five-Tier Breakdown</p>
                <TierBreakdown
                    matchingSkills={analysis.matchingSkills}
                    implicitlyDiscoveredSkills={analysis.implicitlyDiscoveredSkills}
                    prerequisiteMetSkills={analysis.prerequisiteMetSkills}
                    bridgeableSkills={analysis.bridgeableSkills}
                    hardGaps={analysis.hardGaps}
                />
            </div>

            {/* AI Summary */}
            <div className="pt-2 border-t border-slate-100">
                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-3">AI Summary</p>
                {!summary ? (
                    <Button
                        onClick={() => summaryMutation.mutate()}
                        disabled={summaryMutation.isPending}
                        variant="outline"
                        size="sm"
                    >
                        {summaryMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                        Generate Explanation
                    </Button>
                ) : (
                    <Card>
                        <CardContent className="px-4 py-3 space-y-3">
                            <p className="text-sm leading-relaxed text-slate-700">{summary.text}</p>
                            <div className="flex items-center gap-2 text-xs text-slate-400">
                                <span>Graph Grounding: {summary.score.toFixed(2)}</span>
                                <Badge variant="outline" className={summary.score >= 0.95
                                    ? 'border-green-400 text-green-700 bg-green-50'
                                    : 'border-yellow-400 text-yellow-700 bg-yellow-50'
                                }>
                                    {summary.score >= 0.95 ? 'High Confidence' : 'LLM Extrapolation Detected'}
                                </Badge>
                            </div>
                        </CardContent>
                    </Card>
                )}
            </div>

            {/* Resume tailoring */}
            <div className="pt-2 border-t border-slate-100">
                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-3">Resume Tailoring</p>
                {!tailoredBullets ? (
                    <Button
                        onClick={() => tailorMutation.mutate()}
                        disabled={tailorMutation.isPending}
                        variant="outline"
                        size="sm"
                    >
                        {tailorMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                        Tailor My Resume for This Role
                    </Button>
                ) : (
                    <div className="space-y-3">
                        <div className="grid grid-cols-3 gap-4 text-xs font-semibold text-slate-400 uppercase tracking-wider px-1">
                            <span>Original</span><span>Rewritten</span><span>Target Skill</span>
                        </div>
                        {tailoredBullets.map((b, i) => (
                            <Card key={i}>
                                <CardContent className="px-4 py-3 grid grid-cols-3 gap-4">
                                    <p className="text-sm text-slate-400 line-through">{b.originalBullet}</p>
                                    <p className="text-sm font-medium text-slate-800">{b.rewrittenBullet}</p>
                                    <div className="flex flex-col gap-1">
                                        <Badge className="w-fit text-xs">{b.targetSkill}</Badge>
                                        {b.bridgePath && <span className="text-xs text-slate-400">{b.bridgePath}</span>}
                                    </div>
                                </CardContent>
                            </Card>
                        ))}
                    </div>
                )}
            </div>
        </div>
    );
}

// ── Page ──────────────────────────────────────────────────────────────────────

export default function MatchDetail() {
    const { profileId, jobId } = useParams();
    const navigate = useNavigate();

    if (!profileId || !jobId) return null;

    return (
        <div className="space-y-6">
            <button
                onClick={() => navigate(-1)}
                className="flex items-center gap-1.5 text-sm text-slate-500 hover:text-slate-700 transition-colors"
            >
                <ArrowLeft className="h-4 w-4" />
                Back to Matches
            </button>

            <h1 className="text-2xl font-semibold text-slate-900">Match Detail</h1>

            <div className="grid grid-cols-1 lg:grid-cols-[2fr_3fr] gap-6 items-start">
                {/* Left: job info (sticky on desktop) */}
                <div className="lg:sticky lg:top-8">
                    <JobPanel jobId={jobId} />
                </div>

                {/* Right: analysis */}
                <div>
                    <AnalysisPanel profileId={profileId} jobId={jobId} />
                </div>
            </div>
        </div>
    );
}
