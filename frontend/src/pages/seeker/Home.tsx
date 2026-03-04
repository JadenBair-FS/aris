import { useQuery } from '@tanstack/react-query';
import { useNavigate, Link } from 'react-router';
import { useAuth } from '@/context/AuthContext';
import { resumeApi } from '@/api/resume';
import { matchApi } from '@/api/match';
import { MatchCard } from '@/components/MatchCard';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Upload } from 'lucide-react';

export default function SeekerHome() {
    const { user } = useAuth();
    const navigate = useNavigate();

    const { data: profileData, isLoading: isProfileLoading } = useQuery({
        queryKey: ['profileByUser', user?.id],
        queryFn: () => resumeApi.getProfileByUserId(user!.id),
        enabled: !!user,
        retry: false,
    });

    const profileId = profileData?.id;
    const hasResume = profileData?.hasResume ?? false;

    const { data: arisScores, isLoading: isMatchLoading } = useQuery({
        queryKey: ['fastScoresJobs', profileId],
        queryFn: () => matchApi.getFastScoresJobs(profileId!),
        enabled: !!profileId && hasResume,
    });

    if (isProfileLoading) {
        return (
            <div className="space-y-4">
                <Skeleton className="h-8 w-48" />
                <Skeleton className="h-24 w-full" />
                <Skeleton className="h-24 w-full" />
            </div>
        );
    }

    if (!hasResume) {
        return (
            <div className="flex flex-col items-center justify-center min-h-[60vh] text-center">
                <Upload className="h-12 w-12 text-slate-300 mb-4" />
                <h2 className="text-xl font-semibold text-slate-900 mb-2">Upload your resume to see matches</h2>
                <p className="text-slate-500 text-sm mb-6 max-w-sm">
                    Once you upload a resume, ARIS will find matching jobs based on your skills and experience.
                </p>
                <Button asChild className="bg-slate-900 hover:bg-slate-800 text-white">
                    <Link to="/profile/upload">Upload Resume</Link>
                </Button>
            </div>
        );
    }

    const matches = arisScores?.scores ?? [];

    return (
        <div className="space-y-6">
            <div>
                <h1 className="text-2xl font-semibold text-slate-900">
                    Welcome back{user?.name ? `, ${user.name.split(' ')[0]}` : ''}
                </h1>
                <p className="text-slate-500 text-sm mt-1">
                    {isMatchLoading ? 'Finding your best matches...' : `${matches.length} job ${matches.length === 1 ? 'match' : 'matches'} found`}
                </p>
            </div>

            {isMatchLoading && (
                <div className="space-y-3">
                    {[0, 1, 2].map(i => <Skeleton key={i} className="h-24 w-full rounded-xl" />)}
                </div>
            )}

            {!isMatchLoading && matches.length === 0 && (
                <div className="text-center py-16 text-slate-400">
                    <p className="text-sm">No matches found yet.</p>
                    <p className="text-xs mt-1">Try updating your resume or check back later.</p>
                </div>
            )}

            <div className="space-y-3">
                {matches.map((match, index) => (
                    <MatchCard
                        key={match.jobId}
                        index={index}
                        title={match.title}
                        subtitle={match.jobId.slice(0, 8)}
                        cta="Analyze Match"
                        onClick={() => navigate(`/match/${profileId}/${match.jobId}`)}
                    />
                ))}
            </div>
        </div>
    );
}
