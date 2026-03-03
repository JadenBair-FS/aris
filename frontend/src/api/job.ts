import { apiClient } from './client';
import type { JobRecommendationResponse, JobPostingDetail } from '../types/api';

export const jobApi = {
    postJob: (description: string) => {
        return apiClient<{ message: string; jobId: string }>('/job', {
            method: 'POST',
            body: JSON.stringify({ description }),
        });
    },
    getJobsByRecruiter: () => {
        return apiClient<JobPostingDetail[]>('/job/by-recruiter');
    },
    getMatchesForProfile: (userProfileId: string) => {
        return apiClient<JobRecommendationResponse>(`/job/match/${userProfileId}`);
    },
    getJob: (id: string) => {
        return apiClient<JobPostingDetail>(`/job/${id}`);
    },
};
