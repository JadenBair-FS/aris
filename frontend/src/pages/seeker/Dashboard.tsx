import { useAuth } from '@/context/AuthContext';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Link } from 'react-router';
import { Building2, Star, Search, Loader2, ChevronRight, ExternalLink } from 'lucide-react';
import { useQuery } from '@tanstack/react-query';
import { apiClient } from '@/api/client';
import { jobApi } from '@/api/job';
import type { JobRecommendationResponse } from '@/types/api';

export default function Dashboard() {
    const { user } = useAuth();

    // Resolve Clerk UserID to Profile UUID
    const { data: profileData } = useQuery({
        queryKey: ['profileId', user?.id],
        queryFn: async () => {
            const data = await apiClient<any>(`/resume/by-user/${user!.id}`);
            return data.id || data;
        },
        enabled: !!user,
    });

    const profileId = typeof profileData === 'string' ? profileData : profileData;

    // Fetch real job matches from API
    const { data: matchResponse, isLoading: isMatchesLoading } = useQuery({
        queryKey: ['jobMatches', profileId],
        queryFn: () => jobApi.getMatchesForProfile(profileId as string),
        enabled: !!profileId,
    });

    const topMatches = (matchResponse as JobRecommendationResponse | undefined)?.matches?.slice(0, 5) ?? [];

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

                    {isMatchesLoading ? (
                        <div className="flex items-center gap-2 text-sm text-muted-foreground py-8">
                            <Loader2 className="h-4 w-4 animate-spin" /> Loading your matches...
                        </div>
                    ) : topMatches.length === 0 ? (
                        <Card>
                            <CardContent className="p-6 text-center text-sm text-muted-foreground">
                                No matches yet. <Link to="/seeker/upload" className="text-primary hover:underline">Upload your resume</Link> to get started.
                            </CardContent>
                        </Card>
                    ) : (
                        <div className="grid gap-4">
                            {topMatches.map(match => {
                                const signal = match.job?.cleanSignal;
                                const primaryRole = signal?.target_roles?.[0]?.title ?? 'Job Posting';
                                return (
                                    <Card key={match.jobId} className="transition-all hover:shadow-md hover:border-primary/30 border-border shadow-sm">
                                        <CardContent className="p-5">
                                            <div className="flex justify-between items-start gap-4">
                                                <div className="space-y-1">
                                                    <Link
                                                        to={`/seeker/match/${profileId}/${match.jobId}`}
                                                        className="font-semibold text-lg hover:underline decoration-primary"
                                                    >
                                                        {primaryRole}
                                                    </Link>
                                                    <div className="flex items-center gap-3 text-sm text-muted-foreground">
                                                        <span className="flex items-center gap-1.5">
                                                            <Building2 className="h-3.5 w-3.5" />
                                                            <span className="font-mono text-xs">{match.jobId.slice(0, 8)}</span>
                                                        </span>
                                                        {match.job?.sourceUrl && (
                                                            <a
                                                                href={match.job.sourceUrl}
                                                                target="_blank"
                                                                rel="noopener noreferrer"
                                                                className="flex items-center gap-1 text-xs text-primary hover:underline"
                                                                onClick={e => e.stopPropagation()}
                                                            >
                                                                <ExternalLink className="h-3 w-3" /> View posting
                                                            </a>
                                                        )}
                                                    </div>
                                                </div>
                                                <div className="flex items-center gap-2">
                                                    <Button variant="ghost" size="icon" className="h-8 w-8 text-muted-foreground hover:text-yellow-500">
                                                        <Star className="h-4 w-4" />
                                                    </Button>
                                                    <Button variant="outline" size="sm" asChild>
                                                        <Link to={`/seeker/match/${profileId}/${match.jobId}`} className="flex items-center gap-1">
                                                            Analyze <ChevronRight className="h-3.5 w-3.5" />
                                                        </Link>
                                                    </Button>
                                                </div>
                                            </div>
                                        </CardContent>
                                    </Card>
                                );
                            })}
                        </div>
                    )}
                </div>

                {/* Right Sidebar */}
                <div className="space-y-6">

                    <Card className="shadow-sm border-border">
                        <CardHeader className="pb-3">
                            <CardTitle className="text-lg">Resume Status</CardTitle>
                            <CardDescription>Keep your profile updated for better matches</CardDescription>
                        </CardHeader>
                        <CardContent>
                            <Button asChild variant="outline" className="w-full bg-background">
                                <Link to="/seeker/upload">Upload New Resume</Link>
                            </Button>
                        </CardContent>
                    </Card>

                    {matchResponse?.analysis && (
                        <Card>
                            <CardHeader className="pb-3">
                                <CardTitle className="text-lg">AI Match Insight</CardTitle>
                            </CardHeader>
                            <CardContent>
                                <p className="text-sm text-muted-foreground leading-relaxed">{matchResponse.analysis}</p>
                            </CardContent>
                        </Card>
                    )}

                </div>
            </div>
        </div>
    );
}
