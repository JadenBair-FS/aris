import { useState } from 'react';
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { useNavigate } from 'react-router';
import { jobApi } from '@/api/job';
import { useMutation } from '@tanstack/react-query';
import { UploadProgress, JOB_PHASES } from '@/components/UploadProgress';

export default function PostJob() {
    const [description, setDescription] = useState('');
    const [errorText, setErrorText] = useState('');
    const navigate = useNavigate();

    const postJobMutation = useMutation({
        mutationFn: (desc: string) => jobApi.postJob(desc),
        onSuccess: () => navigate('/profile/jobs'),
        onError: (err: any) => setErrorText(err.message),
    });

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        setErrorText('');
        if (!description.trim()) return setErrorText('Please enter a job description.');
        postJobMutation.mutate(description);
    };

    const isPending = postJobMutation.isPending;

    return (
        <div className="max-w-2xl mx-auto space-y-6">
            <UploadProgress isVisible={isPending} phases={JOB_PHASES} />

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
