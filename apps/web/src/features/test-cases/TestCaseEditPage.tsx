import { useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { Skeleton } from '../../components/ui/skeleton';
import { ErrorState } from '../../components/common/ErrorState';
import { ApiError } from '../../lib/api/client';
import { testcaseKeys, testcasesEndpoints, type UpdateTestCaseInput } from '../../lib/api/endpoints/testcases';
import { TestCaseForm } from './TestCaseForm';

export function TestCaseEditPage() {
  const { projectId = '', testCaseId = '' } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [serverError, setServerError] = useState<ApiError | null>(null);

  const testCase = useQuery({
    queryKey: testcaseKeys.details(testCaseId),
    queryFn: () => testcasesEndpoints.get(testCaseId),
    enabled: !!testCaseId,
    retry: false,
  });

  const versions = useQuery({
    queryKey: testcaseKeys.versions(testCaseId),
    queryFn: () => testcasesEndpoints.versions(testCaseId),
    enabled: !!testCaseId,
    retry: false,
  });

  const update = useMutation({
    mutationFn: (input: UpdateTestCaseInput) => testcasesEndpoints.update(testCaseId, input),
    onSuccess: (updated) => {
      void queryClient.invalidateQueries({ queryKey: testcaseKeys.all });
      navigate(`/projects/${projectId}/test-cases/${updated.id}`);
    },
    onError: (error: ApiError) => setServerError(error),
  });

  if (testCase.isLoading || versions.isLoading) {
    return (
      <div className="mx-auto max-w-4xl space-y-4" aria-label="Loading test case">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-96" />
      </div>
    );
  }

  if (testCase.isError || !testCase.data) {
    return (
      <div className="mx-auto max-w-4xl space-y-6">
        <Link to={`/projects/${projectId}/test-cases`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to test cases
        </Link>
        <ErrorState error={testCase.error} onRetry={() => void testCase.refetch()} />
      </div>
    );
  }

  const latest = [...(versions.data ?? [])].sort((a, b) => b.versionNumber - a.versionNumber)[0] ?? null;

  return (
    <div className="mx-auto max-w-4xl space-y-6">
      <div>
        <Link to={`/projects/${projectId}/test-cases/${testCaseId}`} className="text-sm text-brand-700 hover:text-brand-600">
          ← Back to test case
        </Link>
        <h1 className="mt-2 text-xl font-semibold text-slate-900">Edit test case</h1>
        <p className="mt-1 font-mono text-xs text-slate-500">
          {testCase.data.testKey} · metadata edits stay on v{testCase.data.latestVersionNumber}; content
          edits create v{testCase.data.latestVersionNumber + 1}
        </p>
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Test case</CardTitle>
        </CardHeader>
        <CardContent>
          <TestCaseForm
            initial={testCase.data}
            initialContent={
              latest
                ? {
                    sourceCode: latest.sourceCode,
                    structuredSteps: latest.structuredSteps,
                  }
                : null
            }
            submitLabel="Save changes"
            submitting={update.isPending}
            serverError={serverError}
            onSubmit={(values) => {
              setServerError(null);
              const baseCode = latest?.sourceCode ?? '';
              const sourceChanged = values.sourceCode.trim() !== baseCode.trim();
              const baseSteps = (latest?.structuredSteps ?? []).map((s) => ({
                action: s.action,
                target: s.target ?? '',
                value: s.value ?? '',
              }));
              const stepsChanged =
                JSON.stringify(values.structuredSteps) !== JSON.stringify(baseSteps);
              update.mutate({
                title: values.title,
                description: values.description || undefined,
                module: values.module || undefined,
                framework: values.framework || undefined,
                platform: values.platform || undefined,
                priority: values.priority,
                status: values.status,
                sourceType: values.sourceType,
                sourceCode: values.sourceCode || undefined,
                structuredSteps: values.structuredSteps.map((s, i) => ({
                  order: i + 1,
                  action: s.action,
                  target: s.target || null,
                  value: s.value || null,
                })),
                hasSourceCode: sourceChanged,
                hasStructuredSteps: stepsChanged,
              });
            }}
          />
        </CardContent>
      </Card>
    </div>
  );
}
