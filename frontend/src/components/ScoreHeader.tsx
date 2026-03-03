import { Info } from 'lucide-react';
import { Card, CardContent } from './ui/card';

export function ScoreHeader({ arisScore, vectorSimilarity }: { arisScore: number; vectorSimilarity: number }) {
    return (
        <Card className="mb-6">
            <CardContent className="flex items-center justify-between p-6">
                <div className="flex flex-col">
                    <div className="flex items-center gap-2">
                        <span className="text-sm font-medium text-muted-foreground uppercase tracking-wider">ARIS Score</span>
                        <div className="group relative">
                            <Info className="h-4 w-4 text-muted-foreground" />
                            <div className="absolute bottom-full mb-2 hidden group-hover:block w-64 bg-popover text-popover-foreground text-xs rounded p-2 shadow-md z-10 border">
                                ArisScore = 0.55 × VectorSimilarity + 0.45 × GraphCoverageScore.
                            </div>
                        </div>
                    </div>
                    <span className="text-4xl font-bold">{arisScore.toFixed(2)}</span>
                </div>

                <div className="flex flex-col text-right">
                    <span className="text-sm font-medium text-muted-foreground uppercase tracking-wider">Embedding Match</span>
                    <span className="text-2xl font-semibold text-muted-foreground">{vectorSimilarity.toFixed(2)}</span>
                </div>
            </CardContent>
        </Card>
    );
}
