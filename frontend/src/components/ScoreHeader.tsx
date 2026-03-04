import { Info } from 'lucide-react';
import { Card, CardContent } from './ui/card';
import { Badge } from './ui/badge';
import { formatArisScore } from '@/utils/score';

export function ScoreHeader({ arisScore, vectorSimilarity }: { arisScore: number; vectorSimilarity: number }) {
    const scoreBadgeClass =
        arisScore >= 0.75 ? 'bg-green-50 text-green-700 border-green-200' :
        arisScore >= 0.60 ? 'bg-yellow-50 text-yellow-700 border-yellow-200' :
        'bg-slate-100 text-slate-600 border-slate-200';

    return (
        <Card className="mb-6">
            <CardContent className="flex items-center justify-between p-6">
                <div className="flex flex-col gap-1">
                    <div className="flex items-center gap-2">
                        <span className="text-sm font-medium text-muted-foreground uppercase tracking-wider">ARIS Score</span>
                        <div className="group relative">
                            <Info className="h-4 w-4 text-muted-foreground" />
                            <div className="absolute bottom-full mb-2 hidden group-hover:block w-64 bg-popover text-popover-foreground text-xs rounded p-2 shadow-md z-10 border">
                                ArisScore = 0.40 × VectorSimilarity + 0.60 × GraphCoverageScore.
                            </div>
                        </div>
                    </div>
                    <div className="flex items-center gap-2">
                        <span className="text-4xl font-bold">{formatArisScore(arisScore)}</span>
                        <Badge variant="outline" className={`text-xs ${scoreBadgeClass}`}>
                            {arisScore >= 0.75 ? 'Strong Fit' : arisScore >= 0.60 ? 'Moderate Fit' : 'Partial Fit'}
                        </Badge>
                    </div>
                </div>

                <div className="flex flex-col text-right">
                    <span className="text-sm font-medium text-muted-foreground uppercase tracking-wider">Embedding Match</span>
                    <span className="text-2xl font-semibold text-muted-foreground">{vectorSimilarity.toFixed(2)}</span>
                </div>
            </CardContent>
        </Card>
    );
}
