import { useState } from 'react';
import { matchApi } from '@/api/match';
import { TierBreakdown } from '@/components/TierBreakdown';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Skeleton } from '@/components/ui/skeleton';
import type { MatchAnalysisResult } from '@/types/api';

export function QuickMatch({ profileId }: { profileId: string }) {
    const [jobText, setJobText] = useState('');
    const [result, setResult] = useState<MatchAnalysisResult | null>(null);
    const [isLoading, setIsLoading] = useState(false);
    const [error, setError] = useState<string | null>(null);

    const handleAnalyze = async () => {
        if (!profileId || !jobText.trim()) return;
        setIsLoading(true);
        setError(null);
        setResult(null);
        try {
            const data = await matchApi.analyzeQuick(profileId, jobText);
            setResult(data);
        } catch (err) {
            setError(err instanceof Error ? err.message : 'Analysis failed. Please try again.');
        } finally {
            setIsLoading(false);
        }
    };

    const handleClear = () => {
        setJobText('');
        setResult(null);
        setError(null);
    };

    return (
        <div className="space-y-5">
            <div>
                <h2 className="text-lg font-semibold text-slate-900">Quick Job Match</h2>
                <p className="text-sm text-slate-500 mt-1">
                    Paste any job description to see how your resume matches — no account needed for the job.
                </p>
            </div>

            <div className="space-y-3">
                <Textarea
                    placeholder="Paste job description here..."
                    className="min-h-48 w-full resize-y"
                    value={jobText}
                    onChange={e => setJobText(e.target.value)}
                    disabled={isLoading}
                />
                <div className="flex gap-2">
                    <Button
                        onClick={handleAnalyze}
                        disabled={!jobText.trim() || isLoading || !profileId}
                        className="bg-slate-900 hover:bg-slate-800 text-white"
                    >
                        {isLoading ? 'Analyzing...' : 'Analyze Match'}
                    </Button>
                    {(result || error) && (
                        <Button variant="outline" onClick={handleClear} disabled={isLoading}>
                            Clear
                        </Button>
                    )}
                </div>
            </div>

            {isLoading && (
                <div className="space-y-3">
                    <p className="text-sm text-slate-500">Analyzing your match...</p>
                    <Skeleton className="h-12 w-full rounded-lg" />
                    <Skeleton className="h-10 w-full rounded-lg" />
                    <Skeleton className="h-10 w-full rounded-lg" />
                    <Skeleton className="h-10 w-full rounded-lg" />
                </div>
            )}

            {error && !isLoading && (
                <Alert variant="destructive">
                    <AlertDescription>{error}</AlertDescription>
                </Alert>
            )}

            {result && !isLoading && (
                <div className="space-y-4">
                    <div className="flex gap-6 p-4 bg-slate-50 rounded-lg border border-slate-200">
                        <div>
                            <p className="text-xs text-slate-500 uppercase font-medium">ARIS Score</p>
                            <p className="text-2xl font-bold text-slate-900">{(result.arisScore * 100).toFixed(1)}%</p>
                        </div>
                        <div>
                            <p className="text-xs text-slate-500 uppercase font-medium">Vector Similarity</p>
                            <p className="text-2xl font-bold text-slate-900">{(result.vectorSimilarity * 100).toFixed(1)}%</p>
                        </div>
                    </div>

                    <div>
                        <p className="text-xs font-semibold text-slate-400 uppercase tracking-wider mb-3">Five-Tier Breakdown</p>
                        <TierBreakdown
                            matchingSkills={result.matchingSkills}
                            implicitlyDiscoveredSkills={result.implicitlyDiscoveredSkills}
                            prerequisiteMetSkills={result.prerequisiteMetSkills}
                            bridgeableSkills={result.bridgeableSkills}
                            hardGaps={result.hardGaps}
                            ungroundedComparison={result.ungroundedComparison}
                        />
                    </div>
                </div>
            )}
        </div>
    );
}
