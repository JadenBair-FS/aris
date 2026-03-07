import { apiClient } from './client';
import type { JobRecommendationResponse, JobPostingDetail, JobUploadResult, GroundingCorrection } from '../types/api';

export const jobApi = {
    postJob: (description: string, sourceUrl?: string) => {
        return apiClient<JobUploadResult>('/job', {
            method: 'POST',
            body: JSON.stringify({ description, sourceUrl: sourceUrl || null }),
        });
    },
    applyGrounding: (jobId: string, corrections: GroundingCorrection[]) =>
        apiClient<{ message: string }>(`/job/${jobId}/grounding`, {
            method: 'PATCH',
            body: JSON.stringify({ corrections }),
        }),
    getJobsByRecruiter: () => {
        return apiClient<JobPostingDetail[]>('/job/by-recruiter');
    },
    getMatchesForProfile: (userProfileId: string) => {
        return apiClient<JobRecommendationResponse>(`/job/match/${userProfileId}`);
    },
    getJob: (id: string) => {
        return apiClient<JobPostingDetail>(`/job/${id}`);
    },
    deleteJob: (id: string) => {
        return apiClient<{ message: string }>(`/job/${id}`, { method: 'DELETE' });
    },
};
