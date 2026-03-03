import { useAuth } from '@/context/AuthContext';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Link, useNavigate } from 'react-router';
import { Briefcase, Users, Plus, Search } from 'lucide-react';
import { Input } from '@/components/ui/input';
import { useState } from 'react';

export default function Dashboard() {
    const { user } = useAuth();
    const navigate = useNavigate();
    const [searchJobId, setSearchJobId] = useState('');

    // Mock Data for "Your Job Postings"
    const activeJobs = [
        {
            id: 'job-101',
            title: 'Senior Frontend Engineer',
            candidates: 12,
            topArisScore: 92.4,
            posted: '2 days ago'
        },
        {
            id: 'job-102',
            title: 'UI/UX React Developer',
            candidates: 5,
            topArisScore: 84.1,
            posted: '5 hours ago'
        }
    ];

    // Mock Data for "Latest Matched Candidates"
    const recentCandidates = [
        {
            profileId: 'prof-999',
            jobId: 'job-101',
            name: 'Alice Johnson',
            role: 'Frontend Dev',
            matchScore: 92.4,
            time: '1 hour ago'
        },
        {
            profileId: 'prof-888',
            jobId: 'job-101',
            name: 'Bob Smith',
            role: 'React Engineer',
            matchScore: 88.0,
            time: '3 hours ago'
        }
    ];

    const handleSearch = (e: React.FormEvent) => {
        e.preventDefault();
        if (searchJobId.trim()) navigate(`/recruiter/job/${searchJobId.trim()}`);
    }

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
                            View all
                        </Link>
                    </div>

                    <div className="grid gap-4">
                        {activeJobs.map(job => (
                            <Card key={job.id} className="transition-all hover:shadow-md hover:border-primary/30 border-border shadow-sm">
                                <CardContent className="p-5">
                                    <div className="flex justify-between items-start gap-4">
                                        <div className="space-y-1">
                                            <Link to={`/recruiter/job/${job.id}`} className="font-semibold text-lg hover:underline decoration-primary">
                                                {job.title}
                                            </Link>
                                            <div className="text-xs text-muted-foreground font-mono">
                                                ID: {job.id}
                                            </div>
                                        </div>
                                        <Button variant="outline" size="sm" asChild>
                                            <Link to={`/recruiter/job/${job.id}`}>View Details</Link>
                                        </Button>
                                    </div>

                                    <div className="flex flex-wrap items-center mt-5 gap-4 text-sm">
                                        <div className="flex items-center gap-1.5 text-muted-foreground">
                                            <Users className="h-4 w-4" />
                                            <span className="font-medium text-foreground">{job.candidates}</span> Candidates
                                        </div>
                                        <div className="flex items-center gap-1.5 text-muted-foreground">
                                            <Briefcase className="h-4 w-4" />
                                            Top Score: <Badge variant="outline" className="font-mono text-primary border-primary/20 bg-primary/5">{job.topArisScore}</Badge>
                                        </div>
                                        <div className="text-xs text-muted-foreground ml-auto">
                                            Posted {job.posted}
                                        </div>
                                    </div>
                                </CardContent>
                            </Card>
                        ))}
                    </div>
                </div>

                {/* Right Sidebar: Candidates & Search */}
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
                                <Users className="h-4 w-4 text-primary" /> Latest Matches
                            </CardTitle>
                        </CardHeader>
                        <CardContent className="space-y-4">
                            {recentCandidates.map((c, i) => (
                                <div key={i} className="flex justify-between items-center group">
                                    <div>
                                        <Link to={`/recruiter/job/${c.jobId}/candidate/${c.profileId}`} className="text-sm font-semibold group-hover:underline">
                                            {c.name}
                                        </Link>
                                        <p className="text-xs text-muted-foreground">{c.role}</p>
                                    </div>
                                    <div className="text-right">
                                        <Badge variant="outline" className="text-xs text-primary border-primary/20 bg-primary/5">{c.matchScore}</Badge>
                                        <p className="text-[10px] text-muted-foreground mt-1">{c.time}</p>
                                    </div>
                                </div>
                            ))}
                            <Button variant="link" className="w-full h-auto p-0 text-sm font-normal text-muted-foreground">
                                View all candidates
                            </Button>
                        </CardContent>
                    </Card>

                </div>
            </div>
        </div>
    );
}
