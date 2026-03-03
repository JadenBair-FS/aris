import { useParams, useNavigate } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { jobApi } from '@/api/job';
import { recruiterApi } from '@/api/recruiter';
import { Card, CardContent } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs';
import { MatchCard } from '@/components/MatchCard';
import { ArrowLeft } from 'lucide-react';
import { useMemo } from 'react';

export default function JobDetail() {
    const { jobId } = useParams();
    const navigate = useNavigate();

    const { data: job, isLoading: isJobLoading, isError: isJobError } = useQuery({
        queryKey: ['job', jobId],
        queryFn: () => jobApi.getJob(jobId!),
        enabled: !!jobId,
    });

    const { data: searchResponse, isLoading: isCandLoading } = useQuery({
        queryKey: ['candidates', jobId],
        queryFn: () => recruiterApi.getTopCandidates(jobId!, 10),
        enabled: !!jobId,
    });

    const candidates = useMemo(() => {
        if (!searchResponse?.candidates) return [];
        return [...searchResponse.candidates].sort(
            (a, b) => (b.matchAnalysis?.arisScore ?? 0) - (a.matchAnalysis?.arisScore ?? 0)
        );
    }, [searchResponse]);

    const primaryRole = job?.cleanSignal?.target_roles?.[0]?.title ?? 'Job Posting';

    return (
        <div className="space-y-6">
            <button
                onClick={() => navigate(-1)}
                className="flex items-center gap-1.5 text-sm text-slate-500 hover:text-slate-700 transition-colors"
            >
                <ArrowLeft className="h-4 w-4" />
                Back to Jobs
            </button>

            {isJobLoading && <Skeleton className="h-10 w-64" />}
            {isJobError && <p className="text-red-500 text-sm">Failed to load job.</p>}

            {job && (
                <div className="flex items-start justify-between gap-4">
                    <div>
                        <h1 className="text-2xl font-semibold text-slate-900">{primaryRole}</h1>
                        <p className="text-xs text-slate-400 font-mono mt-1">{job.id.slice(0, 8)}</p>
                    </div>
                </div>
            )}

            <Tabs defaultValue="overview">
                <TabsList className="bg-slate-100">
                    <TabsTrigger value="overview">Overview</TabsTrigger>
                    <TabsTrigger value="raw">Source Text</TabsTrigger>
                    <TabsTrigger value="candidates">Top Candidates</TabsTrigger>
                </TabsList>

                {/* Overview tab */}
                <TabsContent value="overview" className="mt-4 space-y-6">
                    {isJobLoading && <Skeleton className="h-40 w-full" />}
                    {job?.cleanSignal && (
                        <>
                            {job.cleanSignal.required_skills.length > 0 && (
                                <Card>
                                    <CardContent className="p-5">
                                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-3">Required Skills</p>
                                        <div className="flex flex-wrap gap-2">
                                            {job.cleanSignal.required_skills.map(s => (
                                                <Badge
                                                    key={s.name}
                                                    variant={s.importance.toLowerCase() === 'essential' ? 'default' : 'secondary'}
                                                    className="text-xs"
                                                >
                                                    {s.name}
                                                    {s.years_of_experience > 0 && ` · ${s.years_of_experience}yr`}
                                                </Badge>
                                            ))}
                                        </div>
                                    </CardContent>
                                </Card>
                            )}

                            {job.cleanSignal.responsibilities.length > 0 && (
                                <Card>
                                    <CardContent className="p-5">
                                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-3">Responsibilities</p>
                                        <ul className="space-y-1.5">
                                            {job.cleanSignal.responsibilities.map((r, i) => (
                                                <li key={i} className="text-sm text-slate-700 flex gap-2">
                                                    <span className="text-slate-300 shrink-0">·</span>
                                                    {r}
                                                </li>
                                            ))}
                                        </ul>
                                    </CardContent>
                                </Card>
                            )}

                            {job.cleanSignal.minimum_education.length > 0 && (
                                <Card>
                                    <CardContent className="p-5">
                                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-3">Education Requirements</p>
                                        <div className="space-y-1">
                                            {job.cleanSignal.minimum_education.map((e, i) => (
                                                <p key={i} className="text-sm text-slate-700">
                                                    {e.degree} {e.required && `— ${e.required}`}
                                                </p>
                                            ))}
                                        </div>
                                    </CardContent>
                                </Card>
                            )}
                        </>
                    )}
                </TabsContent>

                {/* Source text tab */}
                <TabsContent value="raw" className="mt-4">
                    {isJobLoading && <Skeleton className="h-64 w-full" />}
                    {job && (
                        <pre className="bg-slate-50 rounded-lg p-4 text-sm text-slate-700 overflow-auto max-h-96 whitespace-pre-wrap border border-slate-200">
                            {job.rawDescription || 'No source text available.'}
                        </pre>
                    )}
                </TabsContent>

                {/* Candidates tab */}
                <TabsContent value="candidates" className="mt-4 space-y-3">
                    {isCandLoading && (
                        <div className="space-y-3">
                            {[0, 1, 2].map(i => <Skeleton key={i} className="h-24 w-full rounded-xl" />)}
                        </div>
                    )}
                    {!isCandLoading && candidates.length === 0 && (
                        <p className="text-slate-400 text-sm text-center py-8">No candidates found yet.</p>
                    )}
                    {candidates.map((c, index) => (
                        <MatchCard
                            key={c.userProfileId}
                            index={index}
                            title={c.primaryRole || 'Candidate'}
                            subtitle={c.userId.slice(0, 12) + (c.userId.length > 12 ? '...' : '')}
                            arisScore={c.matchAnalysis?.arisScore ?? c.vectorSimilarity}
                            vectorSim={c.vectorSimilarity}
                            tier1Count={c.matchAnalysis?.matchingSkills?.length}
                            hardGapCount={c.matchAnalysis?.hardGaps?.length}
                            onClick={() => navigate(`/candidate/${c.userProfileId}/${jobId}`)}
                        />
                    ))}
                </TabsContent>
            </Tabs>
        </div>
    );
}
