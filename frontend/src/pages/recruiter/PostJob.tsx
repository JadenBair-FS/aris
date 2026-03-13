import { useState } from 'react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { Input } from '@/components/ui/input';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { useNavigate } from 'react-router';
import { jobApi } from '@/api/job';
import { useMutation } from '@tanstack/react-query';
import { UploadProgress, JOB_PHASES } from '@/components/UploadProgress';
import GroundingReviewModal from '@/components/GroundingReviewModal';
import type { JobUploadResult, GroundingCorrection } from '@/types/api';

export default function PostJob() {
    const [description, setDescription] = useState('');
    const [sourceUrl, setSourceUrl] = useState('');
    const [errorText, setErrorText] = useState('');
    const [uploadResult, setUploadResult] = useState<JobUploadResult | null>(null);
    const [showModal, setShowModal] = useState(false);
    const navigate = useNavigate();

    const postJobMutation = useMutation({
        mutationFn: ({ desc, url }: { desc: string; url: string }) =>
            jobApi.postJob(desc, url || undefined),
        onSuccess: (data) => {
            setUploadResult(data);
            setShowModal(true);
        },
        onError: (err: any) => setErrorText(err.message),
    });

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setErrorText('');
        if (!description.trim()) return setErrorText('Please enter a job description.');
        postJobMutation.mutate({ desc: description, url: sourceUrl });
    };

    const handleGroundingConfirm = async (corrections: GroundingCorrection[]) => {
        if (uploadResult && corrections.length > 0) {
            await jobApi.applyGrounding(uploadResult.jobId, corrections);
        }
        navigate('/profile/jobs');
    };

    const isPending = postJobMutation.isPending;
    const cleanSignal = uploadResult?.cleanSignal;
    const groundedTechnical = (cleanSignal?.required_skills.filter(s => s.category !== 'Soft') ?? [])
        .map(s => ({ name: s.name, originalName: s.original_name, category: s.category }));
    const groundedSoft = (cleanSignal?.required_skills.filter(s => s.category === 'Soft') ?? [])
        .map(s => ({ name: s.name, originalName: s.original_name, category: s.category }));
    const ungroundedSkills = (cleanSignal?.ungrounded_skills ?? [])
        .map(s => ({ name: s.name, category: s.category }));

    return (
        <div className="max-w-2xl mx-auto space-y-6">
            <UploadProgress isVisible={isPending} phases={JOB_PHASES} />

            <GroundingReviewModal
                isOpen={showModal}
                groundedTechnical={groundedTechnical}
                groundedSoft={groundedSoft}
                ungroundedSkills={ungroundedSkills}
                onConfirm={handleGroundingConfirm}
                onSkip={() => navigate('/profile/jobs')}
            />

            <div>
                <h1 className="text-2xl font-semibold text-slate-900">Post a Job</h1>
                <p className="text-slate-500 text-sm mt-1">Paste the full job description and ARIS will extract required skills.</p>
            </div>

            {errorText && (
                <Alert variant="destructive">
                    <AlertDescription>{errorText}</AlertDescription>
                </Alert>
            )}

            <Card>
                <CardHeader>
                    <CardTitle className="text-base">Job Description</CardTitle>
                    <CardDescription>Include responsibilities, required skills, and qualifications.</CardDescription>
                </CardHeader>
                <CardContent>
                    <form onSubmit={handleSubmit} className="space-y-4">
                        <div className="space-y-1.5">
                            <label className="text-sm font-medium text-slate-700">Original Posting URL <span className="text-slate-400 font-normal">(optional)</span></label>
                            <Input
                                type="url"
                                placeholder="https://www.linkedin.com/jobs/view/..."
                                value={sourceUrl}
                                onChange={e => setSourceUrl(e.target.value)}
                                disabled={isPending}
                            />
                        </div>
                        <Textarea
                            className="min-h-96 text-sm font-mono"
                            value={description}
                            onChange={e => setDescription(e.target.value)}
                            placeholder="Paste the full job posting here..."
                            disabled={isPending}
                        />
                        <Button
                            type="submit"
                            className="w-full bg-slate-900 hover:bg-slate-800 text-white"
                            disabled={isPending || !description.trim()}
                        >
                            Post & Analyze
                        </Button>
                    </form>
                </CardContent>
            </Card>
        </div>
    );
}
