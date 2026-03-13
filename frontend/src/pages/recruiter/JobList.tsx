import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Link } from 'react-router';
import { jobApi } from '@/api/job';
import { Card, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Skeleton } from '@/components/ui/skeleton';
import { Plus, Briefcase, Trash2 } from 'lucide-react';

export default function JobList() {
    const queryClient = useQueryClient();
    const { data: jobs, isLoading, isError } = useQuery({
        queryKey: ['recruiterJobs'],
        queryFn: () => jobApi.getJobsByRecruiter(),
    });

    const deleteJobMutation = useMutation({
        mutationFn: (id: string) => jobApi.deleteJob(id),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['recruiterJobs'] }),
    });

    if (isLoading) {
        return (
            <div className="space-y-4">
                <Skeleton className="h-10 w-64" />
                <div className="grid grid-cols-2 gap-4">
                    {[0, 1, 2].map(i => <Skeleton key={i} className="h-32 rounded-xl" />)}
                </div>
            </div>
        );
    }

    return (
        <div className="space-y-6">
            <div className="flex items-center justify-between">
                <h1 className="text-2xl font-semibold text-slate-900">Your Job Postings</h1>
                <Button asChild className="bg-slate-900 hover:bg-slate-800 text-white" size="sm">
                    <Link to="/profile/jobs/upload">
                        <Plus className="h-4 w-4 mr-1.5" />
                        Post New Job
                    </Link>
                </Button>
            </div>

            {isError && (
                <p className="text-red-500 text-sm">Failed to load job postings.</p>
            )}

            {jobs && jobs.length === 0 && (
                <div className="flex flex-col items-center justify-center py-20 text-center">
                    <Briefcase className="h-12 w-12 text-slate-300 mb-4" />
                    <p className="text-slate-500 text-sm mb-4">You haven't posted any jobs yet.</p>
                    <Button asChild className="bg-slate-900 hover:bg-slate-800 text-white">
                        <Link to="/profile/jobs/upload">Post your first job</Link>
                    </Button>
                </div>
            )}

            {jobs && jobs.length > 0 && (
                <div className="grid grid-cols-2 gap-4">
                    {jobs.map(job => {
                        const title = job.cleanSignal?.target_roles?.[0]?.title ?? 'Untitled Role';
                        const skillCount = job.cleanSignal?.required_skills?.length ?? 0;
                        const postedDate = job.createdAt
                            ? new Date(job.createdAt).toLocaleDateString()
                            : 'Unknown date';

                        return (
                            <Card key={job.id} className="hover:shadow-md transition-shadow">
                                <CardContent className="px-4 py-3">
                                    <div className="flex items-start justify-between gap-3 mb-3">
                                        <div className="min-w-0">
                                            <h3 className="font-medium text-slate-900 truncate">{title}</h3>
                                            <p className="text-xs text-slate-400 font-mono mt-0.5">{job.id.slice(0, 8)}</p>
                                        </div>
                                        <Badge variant="secondary" className="shrink-0 text-xs">
                                            {skillCount} skills
                                        </Badge>
                                    </div>
                                    <div className="flex items-center justify-between">
                                        <span className="text-xs text-slate-400">Posted {postedDate}</span>
                                        <div className="flex items-center gap-3">
                                            <Link
                                                to={`/profile/jobs/${job.id}`}
                                                className="text-sm font-medium text-slate-700 hover:text-slate-900 transition-colors"
                                            >
                                                View Details →
                                            </Link>
                                            <button
                                                title="Delete job"
                                                disabled={deleteJobMutation.isPending}
                                                onClick={() => {
                                                    if (window.confirm('Delete this job posting?')) {
                                                        deleteJobMutation.mutate(job.id);
                                                    }
                                                }}
                                                className="text-slate-300 hover:text-red-500 transition-colors disabled:opacity-40"
                                            >
                                                <Trash2 className="h-4 w-4" />
                                            </button>
                                        </div>
                                    </div>
                                </CardContent>
                            </Card>
                        );
                    })}
                </div>
            )}
        </div>
    );
}
