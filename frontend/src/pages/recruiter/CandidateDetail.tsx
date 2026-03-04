import { useState } from 'react';
import { useParams, useNavigate } from 'react-router';
import { useQuery, useMutation } from '@tanstack/react-query';
import { matchApi } from '@/api/match';
import { resumeApi } from '@/api/resume';
import { jobApi } from '@/api/job';
import { TierBreakdown } from '@/components/TierBreakdown';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Badge } from '@/components/ui/badge';
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs';
import { ArrowLeft, Loader2, ScanSearch, Info, FileText } from 'lucide-react';
import { formatArisScore } from '@/utils/score';
import type { MatchAnalysisResult, RecruiterSummaryResult, UserProfileDetail } from '@/types/api';

function VerdictBadge({ verdict }: { verdict: string }) {
    const cls =
        verdict === 'Strong Fit'
            ? 'bg-green-50 text-green-700 border-green-300'
            : verdict === 'Not Recommended'
            ? 'bg-red-50 text-red-700 border-red-300'
            : 'bg-amber-50 text-amber-700 border-amber-300';
    return (
        <Badge variant="outline" className={`text-xs px-2 py-0.5 ${cls}`}>{verdict}</Badge>
    );
}

export default function CandidateDetail() {
    const { profileId, jobId } = useParams();
    const navigate = useNavigate();

    const [analysis, setAnalysis] = useState<MatchAnalysisResult | null>(null);
    const [hiringBrief, setHiringBrief] = useState<RecruiterSummaryResult | null>(null);

    const { data: profile, isLoading: isProfileLoading, isError: isProfileError } = useQuery({
        queryKey: ['profile', profileId],
        queryFn: () => resumeApi.getProfile(profileId!),
        enabled: !!profileId,
    });

    const { data: job } = useQuery({
        queryKey: ['job', jobId],
        queryFn: () => jobApi.getJob(jobId!),
        enabled: !!jobId,
    });

    const analyzeMutation = useMutation({
        mutationFn: () => matchApi.analyze(profileId!, jobId!),
        onSuccess: (data) => setAnalysis(data),
    });

    const hiringBriefMutation = useMutation({
        mutationFn: () => matchApi.getRecruiterSummary(profileId!, jobId!),
        onSuccess: (data) => setHiringBrief(data),
    });

    const handleTabChange = (value: string) => {
        if (value === 'analysis' && !analysis && !analyzeMutation.isPending) {
            analyzeMutation.mutate();
        }
    };

    if (!profileId || !jobId) return null;

    const signal = profile?.cleanSignal;
    const primaryRole = signal?.roles.find(r => r.is_current)?.title ?? signal?.roles[0]?.title ?? 'Candidate';

    const skillsByCategory = signal?.skills.reduce<Record<string, NonNullable<UserProfileDetail['cleanSignal']>['skills']>>((acc, skill) => {
        const cat = skill.category || 'Other';
        if (!acc[cat]) acc[cat] = [];
        acc[cat].push(skill);
        return acc;
    }, {}) ?? {};

    const arisScore = analysis?.arisScore ?? 0;
    const scoreBadgeClass =
        arisScore >= 0.75 ? 'bg-green-50 text-green-700 border-green-200' :
        arisScore >= 0.60 ? 'bg-yellow-50 text-yellow-700 border-yellow-200' :
        'bg-slate-100 text-slate-600 border-slate-200';

    return (
        <div className="space-y-4">
            <button
                onClick={() => navigate(-1)}
                className="flex items-center gap-1.5 text-sm text-slate-500 hover:text-slate-700 transition-colors"
            >
                <ArrowLeft className="h-4 w-4" />
                Back to Job
            </button>

            {isProfileLoading ? (
                <Skeleton className="h-20 w-full" />
            ) : isProfileError || !signal ? (
                <p className="text-sm text-red-500">Failed to load candidate profile.</p>
            ) : (
                <Card>
                    <CardContent className="px-6 py-5 flex items-center justify-between gap-4">
                        <div>
                            <h1 className="text-xl font-semibold text-slate-900">{primaryRole}</h1>
                            {signal.roles.length > 1 && (
                                <div className="flex flex-wrap gap-1.5 mt-1.5">
                                    {signal.roles.slice(1).map((r, i) => (
                                        <Badge key={i} variant="secondary" className="text-xs">{r.title}</Badge>
                                    ))}
                                </div>
                            )}
                            <p className="text-slate-400 text-xs font-mono mt-1">{profileId.slice(0, 8)}</p>
                        </div>
                        {job?.cleanSignal && (
                            <div className="text-right shrink-0">
                                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-0.5">Applying for</p>
                                <p className="text-sm font-medium text-slate-700">
                                    {job.cleanSignal.target_roles?.[0]?.title ?? 'Job Posting'}
                                </p>
                                <p className="text-xs text-slate-400 font-mono">{jobId!.slice(0, 8)}</p>
                            </div>
                        )}
                    </CardContent>
                </Card>
            )}

            <Tabs defaultValue="profile" onValueChange={handleTabChange}>
                <TabsList className="bg-slate-100">
                    <TabsTrigger value="profile">Profile</TabsTrigger>
                    <TabsTrigger value="analysis">Analysis</TabsTrigger>
                </TabsList>

                <TabsContent value="profile" className="mt-4">
                    {isProfileLoading ? (
                        <div className="grid grid-cols-2 gap-4">
                            <Skeleton className="h-60" />
                            <Skeleton className="h-60" />
                        </div>
                    ) : signal && (
                        <div className="grid grid-cols-1 lg:grid-cols-2 gap-4 items-start">

                            <div className="space-y-4">
                                {signal.experience_summary.length > 0 && (
                                    <Card>
                                        <CardHeader className="pb-2 pt-4 px-5">
                                            <CardTitle className="text-sm font-semibold text-slate-500 uppercase tracking-wider">Experience</CardTitle>
                                        </CardHeader>
                                        <CardContent className="px-5 pb-4 space-y-4">
                                            {signal.experience_summary.map((exp, i) => (
                                                <div key={i} className={i > 0 ? 'border-t border-slate-100 pt-4' : ''}>
                                                    <p className="font-medium text-slate-900 text-sm">{exp.company}</p>
                                                    <p className="text-sm text-slate-500 mb-1.5">{exp.role}</p>
                                                    {exp.bullets.length > 0 && (
                                                        <ul className="space-y-1">
                                                            {exp.bullets.map((b, j) => (
                                                                <li key={j} className="flex gap-2 text-sm text-slate-600">
                                                                    <span className="text-slate-300 shrink-0 mt-0.5">·</span>
                                                                    {b}
                                                                </li>
                                                            ))}
                                                        </ul>
                                                    )}
                                                </div>
                                            ))}
                                        </CardContent>
                                    </Card>
                                )}
                            </div>

                            <div className="space-y-4">
                                {Object.keys(skillsByCategory).length > 0 && (
                                    <Card>
                                        <CardHeader className="pb-2 pt-4 px-5">
                                            <CardTitle className="text-sm font-semibold text-slate-500 uppercase tracking-wider">Skills</CardTitle>
                                        </CardHeader>
                                        <CardContent className="px-5 pb-4 space-y-4">
                                            {Object.entries(skillsByCategory).map(([category, skills]) => (
                                                <div key={category}>
                                                    <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2">{category}</p>
                                                    <div className="flex flex-wrap gap-1.5">
                                                        {skills.map((s, i) => (
                                                            <span key={i} className="bg-slate-100 text-slate-700 rounded-full px-3 py-1 text-sm">
                                                                {s.name}
                                                                {s.years_of_experience > 0 && (
                                                                    <span className="text-slate-400 ml-1">· {s.years_of_experience}yr</span>
                                                                )}
                                                            </span>
                                                        ))}
                                                    </div>
                                                </div>
                                            ))}
                                        </CardContent>
                                    </Card>
                                )}

                                {signal.education.length > 0 && (
                                    <Card>
                                        <CardHeader className="pb-2 pt-4 px-5">
                                            <CardTitle className="text-sm font-semibold text-slate-500 uppercase tracking-wider">Education</CardTitle>
                                        </CardHeader>
                                        <CardContent className="px-5 pb-4 space-y-3">
                                            {signal.education.map((edu, i) => (
                                                <div key={i} className="flex justify-between items-start">
                                                    <div>
                                                        <p className="text-sm font-medium text-slate-900">{edu.degree}</p>
                                                        <p className="text-sm text-slate-500">{edu.institution}</p>
                                                    </div>
                                                    {edu.year && <p className="text-sm text-slate-400 shrink-0">{edu.year}</p>}
                                                </div>
                                            ))}
                                        </CardContent>
                                    </Card>
                                )}
                            </div>
                        </div>
                    )}
                </TabsContent>

                <TabsContent value="analysis" className="mt-4 space-y-5">
                    {analyzeMutation.isPending && (
                        <div className="flex flex-col items-center justify-center min-h-[360px] gap-3 text-slate-400">
                            <Loader2 className="h-8 w-8 animate-spin" />
                            <p className="text-sm">Running knowledge-graph analysis...</p>
                        </div>
                    )}

                    {analyzeMutation.isError && !analysis && (
                        <div className="flex flex-col items-center justify-center min-h-[360px] rounded-xl border-2 border-dashed border-slate-200 bg-white p-10 text-center">
                            <ScanSearch className="h-10 w-10 text-slate-300 mb-4" />
                            <p className="text-sm text-red-500 mb-4">Analysis failed. Please try again.</p>
                            <Button
                                onClick={() => analyzeMutation.mutate()}
                                className="bg-slate-900 hover:bg-slate-800 text-white px-6"
                                size="lg"
                            >
                                Retry Analysis
                            </Button>
                        </div>
                    )}

                    {analysis && (
                        <>
                            <Card>
                                <CardContent className="px-4 py-3">
                                    <div className="flex items-start justify-between gap-4">
                                        <div>
                                            <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-1">ARIS Score</p>
                                            <div className="flex items-center gap-2">
                                                <span className="text-4xl font-bold text-slate-900">{formatArisScore(arisScore)}</span>
                                                <span className={`text-xs font-medium px-2 py-0.5 rounded-full border ${scoreBadgeClass}`}>
                                                    {arisScore >= 0.75 ? 'Strong fit' : arisScore >= 0.60 ? 'Moderate fit' : 'Partial fit'}
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

                                        <Button
                                            size="sm"
                                            variant="outline"
                                            onClick={() => hiringBriefMutation.mutate()}
                                            disabled={hiringBriefMutation.isPending || !!hiringBrief}
                                            className="flex-shrink-0"
                                        >
                                            {hiringBriefMutation.isPending
                                                ? <><Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" /> Generating...</>
                                                : <><FileText className="mr-1.5 h-3.5 w-3.5" /> Smart Summary</>
                                            }
                                        </Button>
                                    </div>

                                    {hiringBrief && (
                                        <div className="mt-4 pt-4 border-t border-slate-100 space-y-2">
                                            <div className="flex items-center gap-2">
                                                <VerdictBadge verdict={hiringBrief.verdict} />
                                                <span className="text-xs text-slate-400">
                                                    {Math.round(hiringBrief.groundingScore * 100)}% graph-grounded
                                                </span>
                                            </div>
                                            <p className="text-sm leading-relaxed text-slate-700">{hiringBrief.summary}</p>
                                        </div>
                                    )}
                                    {hiringBriefMutation.isError && (
                                        <p className="text-xs text-red-500 mt-2">Failed to generate summary. Please try again.</p>
                                    )}
                                </CardContent>
                            </Card>

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
                        </>
                    )}
                </TabsContent>
            </Tabs>
        </div>
    );
}
