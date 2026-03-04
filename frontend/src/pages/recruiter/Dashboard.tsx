import { useAuth } from '@/context/AuthContext';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Link, useNavigate } from 'react-router';
import { Briefcase, Users, Plus, Search, Loader2 } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { jobApi } from '@/api/job';

export default function Dashboard() {
    const { user } = useAuth();
    const navigate = useNavigate();
    const [searchJobId, setSearchJobId] = useState('');

    const { data: jobPostings, isLoading: isJobsLoading } = useQuery({
        queryKey: ['recruiterJobs'],
        queryFn: () => jobApi.getJobsByRecruiter(),
        enabled: !!user,
    });

    const handleSearch = (e: React.FormEvent) => {
        e.preventDefault();
        if (searchJobId.trim()) navigate(`/recruiter/job/${searchJobId.trim()}`);
    };

    return (
        <div className="space-y-8 animate-in fade-in duration-500">
            <div className="flex flex-col sm:flex-row sm:items-end justify-between gap-4">
                <div>
                    <h1 className="text-3xl font-bold tracking-tight">Hello, {user?.name.split(' ')[0]}!</h1>
                    <p className="text-muted-foreground mt-1">Here is the latest activity on your job postings.</p>
                </div>
                <Button asChild className="gap-2">
                    <Link to="/recruiter/post">
                        <Plus className="h-4 w-4" /> Post New Job
                    </Link>
                </Button>
            </div>

            <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">

                {/* Main Feed: Active Postings */}
                <div className="lg:col-span-2 space-y-6">
                    <div className="flex items-center justify-between">
                        <h2 className="text-xl font-semibold tracking-tight">Your Job Postings</h2>
                        <Link to="/recruiter/post" className="text-sm font-medium text-primary hover:underline">
                            Post new
                        </Link>
                    </div>

                    {isJobsLoading ? (
                        <div className="flex items-center gap-2 text-sm text-muted-foreground py-8">
                            <Loader2 className="h-4 w-4 animate-spin" /> Loading your job postings...
                        </div>
                    ) : !jobPostings || jobPostings.length === 0 ? (
                        <Card>
                            <CardContent className="p-6 text-center text-sm text-muted-foreground">
                                No job postings yet. <Link to="/recruiter/post" className="text-primary hover:underline">Post your first job</Link> to start finding candidates.
                            </CardContent>
                        </Card>
                    ) : (
                        <div className="grid gap-4">
                            {jobPostings.map(job => {
                                const primaryRole = job.cleanSignal?.target_roles?.[0]?.title ?? 'Job Posting';
                                return (
                                    <Card key={job.id} className="transition-all hover:shadow-md hover:border-primary/30 border-border shadow-sm">
                                        <CardContent className="p-5">
                                            <div className="flex justify-between items-start gap-4">
                                                <div className="space-y-1">
                                                    <Link to={`/recruiter/job/${job.id}`} className="font-semibold text-lg hover:underline decoration-primary">
                                                        {primaryRole}
                                                    </Link>
                                                    <div className="text-xs text-muted-foreground font-mono">
                                                        ID: {job.id.slice(0, 8)}
                                                    </div>
                                                </div>
                                                <Button variant="outline" size="sm" asChild>
                                                    <Link to={`/recruiter/job/${job.id}`}>View Candidates</Link>
                                                </Button>
                                            </div>

                                            <div className="flex flex-wrap items-center mt-5 gap-4 text-sm">
                                                <div className="flex items-center gap-1.5 text-muted-foreground">
                                                    <Briefcase className="h-4 w-4" />
                                                    <span className="text-xs font-mono">{job.id.slice(0, 8)}</span>
                                                </div>
                                                {job.cleanSignal?.required_skills && (
                                                    <div className="flex items-center gap-1.5 text-muted-foreground text-xs">
                                                        <Users className="h-3.5 w-3.5" />
                                                        {job.cleanSignal.required_skills.length} required skills
                                                    </div>
                                                )}
                                            </div>
                                        </CardContent>
                                    </Card>
                                );
                            })}
                        </div>
                    )}
                </div>

                {/* Right Sidebar: Search */}
                <div className="space-y-6">

                    <Card>
                        <CardHeader className="pb-3">
                            <CardTitle className="text-lg flex items-center gap-2">
                                <Search className="h-4 w-4" /> Find Existing Job
                            </CardTitle>
                            <CardDescription>Look up a specific job ID</CardDescription>
                        </CardHeader>
                        <CardContent>
                            <form onSubmit={handleSearch} className="flex space-x-2">
                                <Input
                                    placeholder="Enter Job ID..."
                                    value={searchJobId}
                                    onChange={e => setSearchJobId(e.target.value)}
                                />
                                <Button type="submit">Go</Button>
                            </form>
                        </CardContent>
                    </Card>

                    <Card className="shadow-sm border-border">
                        <CardHeader className="pb-3">
                            <CardTitle className="text-lg flex items-center gap-2">
                                <Users className="h-4 w-4 text-primary" /> Find Candidates
                            </CardTitle>
                            <CardDescription>Select a job posting to view matched candidates with ARIS scores</CardDescription>
                        </CardHeader>
                        <CardContent>
                            <p className="text-sm text-muted-foreground">
                                Click "View Candidates" on any job posting above to see ranked candidates with their ARIS match scores.
                            </p>
                        </CardContent>
                    </Card>

                </div>
            </div>
        </div>
    );
}
