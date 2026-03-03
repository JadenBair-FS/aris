import { apiClient } from './client';
import type { CandidateSearchResponse } from '../types/api';

export const recruiterApi = {
    getTopCandidates: (jobId: string, limit: number = 5) => {
        return apiClient<CandidateSearchResponse>(`/recruiter/job/${jobId}/candidates?limit=${limit}`);
    }
};
