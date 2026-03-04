import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, Link } from 'react-router';
import { jobApi } from '@/api/job';
import { matchApi } from '@/api/match';
import { MatchCard } from '@/components/MatchCard';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Briefcase } from 'lucide-react';

export default function RecruiterHome() {
    const navigate = useNavigate();
    const [selectedJobId, setSelectedJobId] = useState<string | undefined>(undefined);

    const { data: jobs, isLoading: isJobsLoading } = useQuery({
        queryKey: ['recruiterJobs'],
        queryFn: () => jobApi.getJobsByRecruiter(),
    });

    const mostRecentJobId = jobs?.[0]?.id;
    const activeJobId = selectedJobId ?? mostRecentJobId;

    const { data: arisScores, isLoading: isCandLoading } = useQuery({
        queryKey: ['fastScoresCandidates', activeJobId],
        queryFn: () => matchApi.getFastScoresCandidates(activeJobId!),
        enabled: !!activeJobId,
    });

    if (isJobsLoading) {
        return (
            <div className="space-y-4">
                <Skeleton className="h-8 w-48" />
                <Skeleton className="h-12 w-full" />
                <Skeleton className="h-24 w-full" />
            </div>
        );
    }

    if (!jobs || jobs.length === 0) {
        return (
            <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
                <Briefcase className="h-12 w-12 text-slate-300 mb-4" />
                <h2 className="text-xl font-semibold text-slate-900 mb-2">Post your first job</h2>
                <p className="text-slate-500 text-sm mb-6 max-w-sm">
                    Once you post a job, ARIS will surface the best-matched candidates from the platform.
                </p>
                <Button asChild className="bg-slate-900 hover:bg-slate-800 text-white">
                    <Link to="/profile/jobs/upload">Post a Job</Link>
                </Button>
            </div>
        );
    }

    const candidates = arisScores?.scores ?? [];

    return (
        <div className="space-y-6">
            <div>
                <h1 className="text-2xl font-semibold text-slate-900">Top Candidates</h1>
                <p className="text-slate-500 text-sm mt-1">Showing matches for the selected job posting</p>
            </div>

            <div>
                <label className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-2 block">Job Posting</label>
                <select
                    value={activeJobId}
                    onChange={e => setSelectedJobId(e.target.value)}
                    className="w-full max-w-sm rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 focus:outline-none focus:ring-2 focus:ring-slate-300"
                >
                    {jobs.map(job => (
                        <option key={job.id} value={job.id}>
                            {job.cleanSignal?.target_roles?.[0]?.title ?? 'Untitled'} — {job.id.slice(0, 8)}
                        </option>
                    ))}
                </select>
            </div>

            {isCandLoading && (
                <div className="space-y-3">
                    {[0, 1, 2].map(i => <Skeleton key={i} className="h-24 w-full rounded-xl" />)}
                </div>
            )}

            {!isCandLoading && candidates.length === 0 && (
                <div className="text-center py-12 text-slate-400 text-sm">
                    No candidates found for this job yet.
                </div>
            )}

            <div className="space-y-3">
                {candidates.map((c, index) => (
                    <MatchCard
                        key={c.userProfileId}
                        index={index}
                        title={c.primaryRole}
                        subtitle={c.userId.slice(0, 12) + (c.userId.length > 12 ? '...' : '')}
                        cta="View Candidate"
                        onClick={() => navigate(`/candidate/${c.userProfileId}/${activeJobId}`)}
                    />
                ))}
            </div>
        </div>
    );
}
