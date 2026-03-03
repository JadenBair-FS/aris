import { useAuth } from '@/context/AuthContext';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Link } from 'react-router';
import { Building2, MapPin, DollarSign, Clock, Star, Search } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/api/client';

export default function Dashboard() {
    const { user } = useAuth();

    // Resolve Clerk UserID to Profile UUID
    useQuery({
        queryKey: ['profileId', user?.id],
        queryFn: async () => {
            try {
                // This returns { id: string } or raw UUID string depending on formatting
                const data = await apiClient<any>(`/resume/by-user/${user!.id}`);
                return data.id || data;
            } catch {
                return user!.id; // fallback for mock
            }
        },
        enabled: !!user,
    });

    // Mock Data for "Jobs For You"
    const mockMatches = [
        {
            id: 'job-101',
            title: 'Senior Frontend Engineer',
            company: 'Acme Corp',
            location: 'Remote',
            salary: '$140k - $160k',
            type: 'Full-time',
            matchScore: 92,
            posted: '2 days ago'
        },
        {
            id: 'job-102',
            title: 'UI/UX React Developer',
            company: 'GlobalTech',
            location: 'New York, NY',
            salary: '$120k - $140k',
            type: 'Hybrid',
            matchScore: 85,
            posted: '5 hours ago'
        }
    ];

    // Mock Data for "Favorited Jobs"
    const favoritedJobs = [
        {
            id: 'job-201',
            title: 'Frontend Architect',
            company: 'TechStart Inc.',
        }
    ];

    return (
        <div className="space-y-8 animate-in fade-in duration-500">
            <div className="flex flex-col sm:flex-row sm:items-end justify-between gap-4">
                <div>
                    <h1 className="text-3xl font-bold tracking-tight">Welcome back, {user?.name.split(' ')[0]}!</h1>
                    <p className="text-muted-foreground mt-1">Here is what's happening with your job search today.</p>
                </div>
                <Button asChild className="gap-2">
                    <Link to={`/seeker/profile/${user?.id}`}>
                        <Search className="h-4 w-4" /> View My Deep Matches
                    </Link>
                </Button>
            </div>

            <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">

                {/* Main Feed: Top Matches */}
                <div className="lg:col-span-2 space-y-6">
                    <div className="flex items-center justify-between">
                        <h2 className="text-xl font-semibold tracking-tight">Top Matches For You</h2>
                        <Link to={`/seeker/profile/${user?.id}`} className="text-sm font-medium text-primary hover:underline">
                            View all
                        </Link>
                    </div>

                    <div className="grid gap-4">
                        {mockMatches.map(job => (
                            <Card key={job.id} className="transition-all hover:shadow-md hover:border-primary/30 border-border shadow-sm">
                                <CardContent className="p-5">
                                    <div className="flex justify-between items-start gap-4">
                                        <div className="space-y-1">
                                            <Link to={`/seeker/match/${user?.id}/${job.id}`} className="font-semibold text-lg hover:underline decoration-primary">
                                                {job.title}
                                            </Link>
                                            <div className="flex items-center text-sm text-muted-foreground gap-1.5">
                                                <Building2 className="h-3.5 w-3.5" />
                                                <span className="font-medium text-foreground">{job.company}</span>
                                            </div>
                                        </div>
                                        <div className="flex flex-col items-end gap-2">
                                            <Badge variant="outline" className="text-primary border-primary/20 bg-primary/5">
                                                {job.matchScore}% Match
                                            </Badge>
                                            <Button variant="ghost" size="icon" className="h-8 w-8 text-muted-foreground hover:text-yellow-500">
                                                <Star className="h-4 w-4" />
                                            </Button>
                                        </div>
                                    </div>

                                    <div className="flex flex-wrap gap-x-4 gap-y-2 mt-4 text-xs text-muted-foreground">
                                        <span className="flex items-center gap-1"><MapPin className="h-3.5 w-3.5" /> {job.location}</span>
                                        <span className="flex items-center gap-1"><DollarSign className="h-3.5 w-3.5" /> {job.salary}</span>
                                        <span className="flex items-center gap-1"><Clock className="h-3.5 w-3.5" /> {job.type}</span>
                                    </div>
                                </CardContent>
                            </Card>
                        ))}
                    </div>
                </div>

                {/* Right Sidebar: Status & Favorites */}
                <div className="space-y-6">

                    <Card className="shadow-sm border-border">
                        <CardHeader className="pb-3">
                            <CardTitle className="text-lg">Resume Status</CardTitle>
                            <CardDescription>Keep your profile updated for better matches</CardDescription>
                        </CardHeader>
                        <CardContent>
                            <div className="flex items-center justify-between text-sm mb-4">
                                <span className="text-muted-foreground">Last updated:</span>
                                <span className="font-medium">2 days ago</span>
                            </div>
                            <Button asChild variant="outline" className="w-full bg-background">
                                <Link to="/seeker/upload">Upload New Resume</Link>
                            </Button>
                        </CardContent>
                    </Card>

                    <Card>
                        <CardHeader className="pb-3">
                            <CardTitle className="text-lg flex items-center gap-2">
                                <Star className="h-4 w-4 fill-yellow-400 text-yellow-500" /> Favorited Jobs
                            </CardTitle>
                        </CardHeader>
                        <CardContent className="space-y-4">
                            {favoritedJobs.map(job => (
                                <div key={job.id} className="flex justify-between items-center group">
                                    <div>
                                        <h4 className="text-sm font-semibold group-hover:underline cursor-pointer">{job.title}</h4>
                                        <p className="text-xs text-muted-foreground">{job.company}</p>
                                    </div>
                                    <Button variant="ghost" size="sm" className="h-7 px-2 text-xs">View</Button>
                                </div>
                            ))}
                            <Button variant="link" className="w-full h-auto p-0 text-sm font-normal text-muted-foreground">
                                View all favorites
                            </Button>
                        </CardContent>
                    </Card>

                </div>
            </div>
        </div>
    );
}
