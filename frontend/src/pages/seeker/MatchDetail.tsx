import { useState } from 'react';
import { useParams, useNavigate } from 'react-router';
import { useQuery, useMutation } from '@tanstack/react-query';
import { useAuth } from '@/context/AuthContext';
import { matchApi } from '@/api/match';
import { jobApi } from '@/api/job';
import { resumeApi } from '@/api/resume';
import { TierBreakdown } from '@/components/TierBreakdown';
import { SkillBadge } from '@/components/SkillBadge';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Badge } from '@/components/ui/badge';
import { toast } from 'sonner';
import { ArrowLeft, Loader2, ScanSearch, Info, Wand2, ExternalLink, Sparkles, ChevronDown, ChevronUp } from 'lucide-react';
import { formatArisScore } from '@/utils/score';
import type { MatchAnalysisResult } from '@/types/api';

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
            <Card>
                <CardContent className="px-4 py-3">
                    <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0">
                            <h2 className="text-base font-semibold text-slate-900">{primaryRole}</h2>
                            <p className="text-xs text-slate-400 font-mono mt-0.5">{job.id.slice(0, 8)}</p>
                        </div>
                        {job.sourceUrl && (
                            <a
                                href={job.sourceUrl}
                                target="_blank"
                                rel="noopener noreferrer"
                            >
                                <Button variant="outline" size="sm" className="shrink-0 gap-1.5">
                                    <ExternalLink className="h-3.5 w-3.5" /> View Posting
                                </Button>
                            </a>
                        )}
                    </div>
                    {signal?.target_roles && signal.target_roles.length > 1 && (
                        <div className="flex flex-wrap gap-1.5 mt-2">
                            {signal.target_roles.slice(1).map((r, i) => (
                                <Badge key={i} variant="secondary" className="text-xs">{r.title}</Badge>
                            ))}
                        </div>
                    )}
                </CardContent>
            </Card>

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

            {signal && (essential.length > 0 || preferred.length > 0) && (
                <Card>
                    <CardContent className="px-4 py-3 space-y-3">
                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider">Skills</p>
                        {essential.length > 0 && (
                            <div>
                                <p className="text-xs text-slate-400 mb-1.5">Essential</p>
                                <div className="flex flex-wrap gap-1.5">
                                    {essential.map(s => (
                                        <SkillBadge
                                            key={s.name}
                                            name={s.name}
                                            originalName={s.original_name}
                                            yearsOfExperience={s.years_of_experience}
                                            className="bg-slate-900 text-white text-xs"
                                        />
                                    ))}
                                </div>
                            </div>
                        )}
                        {preferred.length > 0 && (
                            <div>
                                <p className="text-xs text-slate-400 mb-1.5">Preferred</p>
                                <div className="flex flex-wrap gap-1.5">
                                    {preferred.map(s => (
                                        <SkillBadge
                                            key={s.name}
                                            name={s.name}
                                            originalName={s.original_name}
                                            yearsOfExperience={s.years_of_experience}
                                            className="text-xs"
                                        />
                                    ))}
                                </div>
                            </div>
                        )}
                        {signal.ungrounded_skills && signal.ungrounded_skills.length > 0 && (
                            <div>
                                <p className="text-xs text-slate-400 mb-1.5">Not in Database</p>
                                <div className="flex flex-wrap gap-1.5">
                                    {signal.ungrounded_skills.map(s => (
                                        <SkillBadge
                                            key={s.name}
                                            name={s.name}
                                            originalName={s.original_name}
                                            yearsOfExperience={s.years_of_experience}
                                            className="text-xs border border-dashed border-slate-300 bg-transparent text-slate-500"
                                        />
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

function AnalysisPanel({
    profileId,
    jobId,
}: {
    profileId: string;
    jobId: string;
}) {
    const { user } = useAuth();
    const { data: job } = useQuery({
        queryKey: ['job', jobId],
        queryFn: () => jobApi.getJob(jobId),
    });

    const [analysis, setAnalysis] = useState<MatchAnalysisResult | null>(null);
    const [explanation, setExplanation] = useState<string | null>(null);
    const [explanationOpen, setExplanationOpen] = useState(false);

    const analyzeMutation = useMutation({
        mutationFn: () => matchApi.analyze(profileId, jobId),
        onSuccess: (data) => setAnalysis(data),
    });

    const explainMutation = useMutation({
        mutationFn: () => matchApi.explain(profileId, jobId),
        onSuccess: (data) => {
            setExplanation(data.explanation);
            setExplanationOpen(true);
        },
        onError: () => toast.error('Could not generate explanation. Please try again.'),
    });

    const tailorMutation = useMutation({
        mutationFn: () => resumeApi.tailorPdf(profileId, jobId, analysis ?? undefined),
        onSuccess: (blob) => {
            const slugify = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '');
            const userName = slugify(user?.name ?? 'resume');
            const jobTitle = slugify(job?.cleanSignal?.target_roles?.[0]?.title ?? jobId.slice(0, 8));
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `${userName}-${jobTitle}.pdf`;
            a.click();
            URL.revokeObjectURL(url);
            toast.success('Your tailored resume is downloading.');
        },
        onError: () => {
            toast.error('Failed to generate resume. Please try again.');
        },
    });

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

    const arisScore = analysis.arisScore;
    const scoreColorClass =
        arisScore >= 0.65 ? 'text-green-600' :
        arisScore >= 0.40 ? 'text-orange-500' :
        'text-red-600';
    const scoreBadgeClass =
        arisScore >= 0.65 ? 'bg-green-50 text-green-700 border-green-200' :
        arisScore >= 0.40 ? 'bg-orange-50 text-orange-700 border-orange-200' :
        'bg-red-50 text-red-700 border-red-200';
    const scoreBadgeLabel =
        arisScore >= 0.65 ? 'Well Qualified' :
        arisScore >= 0.40 ? 'Partial Match' :
        'Significant Gaps';

    return (
        <div className="space-y-5">
            <Card>
                <CardContent className="px-4 py-3">
                    <div className="flex items-start justify-between gap-4">
                        <div>
                            <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-1">ARIS Score</p>
                            <div className="flex items-center gap-2">
                                <span className={`text-4xl font-bold ${scoreColorClass}`}>{formatArisScore(arisScore)}</span>
                                <span className={`text-xs font-medium px-2 py-0.5 rounded-full border ${scoreBadgeClass}`}>
                                    {scoreBadgeLabel}
                                </span>
                            </div>
                            <div className="flex items-center gap-1 mt-1">
                                <p className="text-xs text-slate-400">Embedding: {analysis.vectorSimilarity.toFixed(2)}</p>
                                <div className="group relative">
                                    <Info className="h-3 w-3 text-slate-300 cursor-help" />
                                    <div className="absolute left-0 top-4 hidden group-hover:block w-56 bg-white text-slate-600 text-xs rounded-lg p-2.5 shadow-lg z-10 border border-slate-100">
                                        ArisScore = 0.40 × VectorSimilarity + 0.60 × GraphCoverageScore
                                    </div>
                                </div>
                            </div>
                        </div>

                        <div className="flex items-center gap-2 flex-shrink-0">
                            <Button
                                size="sm"
                                variant="outline"
                                onClick={() => tailorMutation.mutate()}
                                disabled={tailorMutation.isPending}
                            >
                                {tailorMutation.isPending
                                    ? <><Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> Tailoring...</>
                                    : <><Wand2 className="mr-1.5 h-3.5 w-3.5" /> Tailor Resume</>
                                }
                            </Button>
                        </div>
                    </div>

                    <div className="mt-3 pt-3 border-t border-slate-100">
                        <Button
                            size="sm"
                            variant="ghost"
                            className="w-full text-slate-600 hover:text-slate-900 hover:bg-slate-50 text-xs gap-1.5"
                            onClick={() => explainMutation.mutate()}
                            disabled={explainMutation.isPending}
                        >
                            {explainMutation.isPending
                                ? <><Loader2 className="h-3.5 w-3.5 animate-spin" /> Generating explanation...</>
                                : explanation
                                    ? <><ChevronUp className="h-3.5 w-3.5" />{explanationOpen ? 'Hide' : 'Show'} AI Explanation</>
                                    : <><Sparkles className="h-3.5 w-3.5" /> Explain this score</>
                            }
                        </Button>
                        {explanation && !explainMutation.isPending && (
                            <button
                                className="w-full text-left text-xs text-slate-400 flex items-center justify-end gap-1 mt-1 hover:text-slate-600"
                                onClick={() => setExplanationOpen(o => !o)}
                            >
                                {explanationOpen ? <ChevronUp className="h-3 w-3" /> : <ChevronDown className="h-3 w-3" />}
                                {explanationOpen ? 'collapse' : 'expand'}
                            </button>
                        )}
                    </div>
                </CardContent>
            </Card>

            {explanation && explanationOpen && (
                <Card className="border-slate-200 bg-slate-50">
                    <CardContent className="px-4 py-3">
                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2">AI Match Explanation</p>
                        <p className="text-sm text-slate-700 whitespace-pre-wrap leading-relaxed">{explanation}</p>
                    </CardContent>
                </Card>
            )}

            <div>
                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-3">Five-Tier Breakdown</p>
                <TierBreakdown
                    matchingSkills={analysis.matchingSkills}
                    implicitlyDiscoveredSkills={analysis.implicitlyDiscoveredSkills}
                    prerequisiteMetSkills={analysis.prerequisiteMetSkills}
                    bridgeableSkills={analysis.bridgeableSkills}
                    hardGaps={analysis.hardGaps}
                    ungroundedComparison={analysis.ungroundedComparison}
                />
            </div>

            {tailorMutation.isSuccess && (
                <p className="text-xs text-slate-400 text-center pt-1">
                    Resume downloaded. Run again anytime to regenerate.
                </p>
            )}
        </div>
    );
}

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
                <div className="lg:sticky lg:top-8">
                    <JobPanel jobId={jobId} />
                </div>
                <div>
                    <AnalysisPanel profileId={profileId} jobId={jobId} />
                </div>
            </div>
        </div>
    );
}
