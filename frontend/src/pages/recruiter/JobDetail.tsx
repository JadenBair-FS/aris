import { useParams, useNavigate, Link } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import { jobApi } from '@/api/job';
import { matchApi } from '@/api/match';
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Tabs, TabsList, TabsTrigger, TabsContent } from '@/components/ui/tabs';
import { MatchCard } from '@/components/MatchCard';
import { SkillBadge } from '@/components/SkillBadge';
import { ArrowLeft } from 'lucide-react';

export default function JobDetail() {
    const { jobId } = useParams();
    const navigate = useNavigate();

    const { data: job, isLoading: isJobLoading, isError: isJobError } = useQuery({
        queryKey: ['job', jobId],
        queryFn: () => jobApi.getJob(jobId!),
        enabled: !!jobId,
    });

    const { data: arisScores, isLoading: isCandLoading } = useQuery({
        queryKey: ['fastScoresCandidates', jobId],
        queryFn: () => matchApi.getFastScoresCandidates(jobId!),
        enabled: !!jobId,
    });

    const candidates = arisScores?.scores ?? [];

    const signal = job?.cleanSignal;
    const primaryRole = signal?.target_roles?.[0]?.title ?? 'Job Posting';

    const essential = signal?.required_skills?.filter(s => s.importance.toLowerCase() === 'essential') ?? [];
    const preferred = signal?.required_skills?.filter(s => s.importance.toLowerCase() !== 'essential') ?? [];

    if (isJobLoading) {
        return (
            <div className="space-y-4">
                <Skeleton className="h-24 w-full" />
                <Skeleton className="h-20 w-full" />
                <div className="grid grid-cols-2 gap-4">
                    <Skeleton className="h-60" />
                    <Skeleton className="h-60" />
                </div>
            </div>
        );
    }

    if (isJobError || !job) {
        return <p className="text-sm text-red-500">Failed to load job.</p>;
    }

    return (
        <div className="space-y-4">
            <button
                onClick={() => navigate(-1)}
                className="flex items-center gap-1.5 text-sm text-slate-500 hover:text-slate-700 transition-colors"
            >
                <ArrowLeft className="h-4 w-4" />
                Back to Jobs
            </button>

            <Card>
                <CardContent className="px-6 py-5 flex items-center justify-between gap-4">
                    <div>
                        <h1 className="text-xl font-semibold text-slate-900">{primaryRole}</h1>
                        {signal?.target_roles && signal.target_roles.length > 1 && (
                            <div className="flex flex-wrap gap-1.5 mt-1.5">
                                {signal.target_roles.slice(1).map((r, i) => (
                                    <Badge key={i} variant="secondary" className="text-xs">{r.title}</Badge>
                                ))}
                            </div>
                        )}
                        <p className="text-slate-400 text-xs font-mono mt-1">{job.id.slice(0, 8)}</p>
                    </div>
                    <Button asChild variant="outline" size="sm" className="shrink-0">
                        <Link to="/recruiter/post">Post New Job</Link>
                    </Button>
                </CardContent>
            </Card>

            <Tabs defaultValue="overview">
                <TabsList className="bg-slate-100">
                    <TabsTrigger value="overview">Overview</TabsTrigger>
                    <TabsTrigger value="candidates">Candidates</TabsTrigger>
                </TabsList>

                <TabsContent value="overview" className="mt-4">
                    <div className="grid grid-cols-1 lg:grid-cols-2 gap-4 items-start">

                        <div className="space-y-4">
                            {signal?.responsibilities && signal.responsibilities.length > 0 && (
                                <Card>
                                    <CardHeader className="pb-2 pt-4 px-5">
                                        <CardTitle className="text-sm font-semibold text-slate-500 uppercase tracking-wider">Description</CardTitle>
                                    </CardHeader>
                                    <CardContent className="px-5 pb-4">
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
                        </div>

                        <div className="space-y-4">
                            {(essential.length > 0 || preferred.length > 0) && (
                                <Card>
                                    <CardHeader className="pb-2 pt-4 px-5">
                                        <CardTitle className="text-sm font-semibold text-slate-500 uppercase tracking-wider">Required Skills</CardTitle>
                                    </CardHeader>
                                    <CardContent className="px-5 pb-4 space-y-4">
                                        {essential.length > 0 && (
                                            <div>
                                                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2">Essential</p>
                                                <div className="flex flex-wrap gap-1.5">
                                                    {essential.map(s => (
                                                        <SkillBadge
                                                            key={s.name}
                                                            name={s.name}
                                                            originalName={s.original_name}
                                                            yearsOfExperience={s.years_of_experience}
                                                            className="bg-slate-900 text-white"
                                                        />
                                                    ))}
                                                </div>
                                            </div>
                                        )}
                                        {preferred.length > 0 && (
                                            <div>
                                                <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2">Preferred</p>
                                                <div className="flex flex-wrap gap-1.5">
                                                    {preferred.map(s => (
                                                        <SkillBadge
                                                            key={s.name}
                                                            name={s.name}
                                                            originalName={s.original_name}
                                                            yearsOfExperience={s.years_of_experience}
                                                        />
                                                    ))}
                                                </div>
                                            </div>
                                        )}
                                    </CardContent>
                                </Card>
                            )}

                            {signal?.minimum_education && signal.minimum_education.length > 0 && (
                                <Card>
                                    <CardHeader className="pb-2 pt-4 px-5">
                                        <CardTitle className="text-sm font-semibold text-slate-500 uppercase tracking-wider">Education Requirements</CardTitle>
                                    </CardHeader>
                                    <CardContent className="px-5 pb-4 space-y-2">
                                        {signal.minimum_education.map((e, i) => (
                                            <div key={i} className="flex justify-between items-start">
                                                <p className="text-sm font-medium text-slate-900">{e.degree}</p>
                                                {e.required && <p className="text-sm text-slate-500">{e.required}</p>}
                                            </div>
                                        ))}
                                    </CardContent>
                                </Card>
                            )}
                        </div>
                    </div>
                </TabsContent>

                <TabsContent value="candidates" className="mt-4 space-y-3">
                    {isCandLoading && (
                        <div className="space-y-3">
                            {[0, 1, 2].map(i => <Skeleton key={i} className="h-16 w-full rounded-xl" />)}
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
                            cta="View Candidate"
                            onClick={() => navigate(`/candidate/${c.userProfileId}/${jobId}`)}
                        />
                    ))}
                </TabsContent>
            </Tabs>
        </div>
    );
}
