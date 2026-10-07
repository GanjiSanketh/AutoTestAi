import { useState, useEffect } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { Plus, X, GripVertical, AlertCircle } from 'lucide-react';
import { Button } from '../../components/ui/button';
import { Input } from '../../components/ui/input';
import { Textarea } from '../../components/ui/textarea';
import { Badge } from '../../components/ui/badge';
import { Card, CardContent, CardHeader, CardTitle } from '../../components/ui/card';
import { ErrorState } from '../../components/common/ErrorState';
import { suiteKeys, suitesEndpoints, type CreateSuiteInput, type UpdateSuiteInput, type ReorderSuiteMembersInput } from '../../lib/api/endpoints/suites';
import { testcaseKeys, testcasesEndpoints, type TestCaseListItem } from '../../lib/api/endpoints/testcases';
import { useProfile } from '../../lib/auth/useProfile';
import { Permissions, hasPermission } from '../../lib/auth/permissions';

function statusTone(status: string): 'success' | 'warning' | 'info' | 'neutral' {
  switch (status) {
    case 'Active':
      return 'success';
    case 'Archived':
      return 'neutral';
    default:
      return 'info';
  }
}

const STATUS_OPTIONS = ['Active', 'Archived'];

interface SelectedTestCase {
  id: string;
  testKey: string;
  title: string;
  executionOrder: number;
}

export function SuiteFormPage() {
  const { projectId = '', suiteId } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const profile = useProfile();
  const canManage = hasPermission(profile.data?.permissions, Permissions.TestCasesManage);
  const isEdit = !!suiteId;

  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [status, setStatus] = useState('Active');
  const [selectedCases, setSelectedCases] = useState<SelectedTestCase[]>([]);
  const [search, setSearch] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [submitting, setSubmitting] = useState(false);

  const availableCases = useQuery({
    queryKey: testcaseKeys.list(projectId, { status: 'Active' }, 1),
    queryFn: () => testcasesEndpoints.list(projectId, { status: 'Active' }, 1, 100),
    enabled: !!projectId,
    staleTime: 30_000,
  });

  const suite = useQuery({
    queryKey: suiteKeys.details(suiteId!),
    queryFn: () => suitesEndpoints.get(suiteId!),
    enabled: isEdit,
    staleTime: 0,
  });

  useEffect(() => {
    if (isEdit && suite.data) {
      setName(suite.data.name);
      setDescription(suite.data.description ?? '');
      setStatus(suite.data.status);
      setSelectedCases(suite.data.members.map((m, i) => ({
        id: m.testCaseId,
        testKey: m.testKey,
        title: m.title,
        executionOrder: m.executionOrder ?? i + 1,
      })));
    }
  }, [isEdit, suite.data]);

  const createMutation = useMutation({
    mutationFn: (input: CreateSuiteInput) => suitesEndpoints.create(projectId, input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: suiteKeys.all });
      navigate(`/projects/${projectId}/test-suites`);
    },
    onError: (error: any) => {
      if (error?.response?.data?.errors) {
        const newErrors: Record<string, string> = {};
        for (const err of error.response.data.errors) {
          newErrors[err.field] = err.message;
        }
        setErrors(newErrors);
      } else {
        setErrors({ form: error.message });
      }
      setSubmitting(false);
    },
  });

  const updateMutation = useMutation({
    mutationFn: (input: UpdateSuiteInput) => suitesEndpoints.update(suiteId!, input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: suiteKeys.all });
      navigate(`/projects/${projectId}/test-suites/${suiteId}`);
    },
    onError: (error: any) => {
      if (error?.response?.data?.errors) {
        const newErrors: Record<string, string> = {};
        for (const err of error.response.data.errors) {
          newErrors[err.field] = err.message;
        }
        setErrors(newErrors);
      } else {
        setErrors({ form: error.message });
      }
      setSubmitting(false);
    },
  });

  const removeMemberMutation = useMutation({
    mutationFn: (testCaseId: string) => suitesEndpoints.removeTestCase(suiteId!, testCaseId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: suiteKeys.details(suiteId!) });
      queryClient.invalidateQueries({ queryKey: suiteKeys.all });
    },
    onError: (error: any) => {
      setErrors({ form: error.message ?? 'Failed to remove test case.' });
    },
  });

  const addMemberMutation = useMutation({
    mutationFn: (input: { testCaseId: string; executionOrder: number }) =>
      suitesEndpoints.addTestCase(suiteId!, input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: suiteKeys.details(suiteId!) });
      queryClient.invalidateQueries({ queryKey: suiteKeys.all });
    },
    onError: (error: any) => {
      setErrors({ form: error.message ?? 'Failed to add test case.' });
    },
  });

  const reorderMutation = useMutation({
    mutationFn: (input: ReorderSuiteMembersInput) => suitesEndpoints.reorder(suiteId!, input),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: suiteKeys.details(suiteId!) });
    },
  });

  const handleSubmit = (e: React.FormEvent | React.MouseEvent) => {
    e.preventDefault();
    setErrors({});
    setSubmitting(true);

    if (!name.trim()) {
      setErrors({ name: 'Suite name is required.' });
      setSubmitting(false);
      return;
    }
    if (name.trim().length > 200) {
      setErrors({ name: 'Suite name must be at most 200 characters.' });
      setSubmitting(false);
      return;
    }

    if (isEdit) {
      updateMutation.mutate({ name: name.trim(), description: description.trim() || undefined, status });
    } else {
      createMutation.mutate({
        name: name.trim(),
        description: description.trim() || undefined,
        status,
        members: selectedCases.map((c, i) => ({ testCaseId: c.id, executionOrder: c.executionOrder ?? i + 1 })),
      });
    }
  };

  const addTestCase = (testCase: { id: string; testKey: string; title: string }) => {
    if (selectedCases.some(c => c.id === testCase.id)) return;
    const nextOrder = selectedCases.length > 0 ? Math.max(...selectedCases.map(c => c.executionOrder)) + 1 : 1;
    if (!isEdit) {
      setSelectedCases([...selectedCases, { ...testCase, executionOrder: nextOrder }]);
      return;
    }
    addMemberMutation.mutate(
      { testCaseId: testCase.id, executionOrder: nextOrder },
      {
        onSuccess: () => {
          setSelectedCases([...selectedCases, { ...testCase, executionOrder: nextOrder }]);
        },
      },
    );
  };

  const removeTestCase = (testCaseId: string) => {
    if (!isEdit) {
      setSelectedCases(selectedCases.filter(c => c.id !== testCaseId).map((c, i) => ({ ...c, executionOrder: i + 1 })));
      return;
    }
    removeMemberMutation.mutate(testCaseId, {
      onSuccess: () => {
        setSelectedCases(selectedCases.filter(c => c.id !== testCaseId).map((c, i) => ({ ...c, executionOrder: i + 1 })));
      },
    });
  };

  const moveUp = (index: number) => {
    if (index === 0) return;
    const newCases = [...selectedCases];
    [newCases[index - 1], newCases[index]] = [newCases[index], newCases[index - 1]];
    setSelectedCases(newCases.map((c, i) => ({ ...c, executionOrder: i + 1 })));
  };

  const moveDown = (index: number) => {
    if (index === selectedCases.length - 1) return;
    const newCases = [...selectedCases];
    [newCases[index], newCases[index + 1]] = [newCases[index + 1], newCases[index]];
    setSelectedCases(newCases.map((c, i) => ({ ...c, executionOrder: i + 1 })));
  };

  const saveOrder = () => {
    if (!isEdit) return;
    reorderMutation.mutate({
      members: selectedCases.map(c => ({ testCaseId: c.id, executionOrder: c.executionOrder })),
    });
  };

  const filteredCases = availableCases.data?.items.filter(c =>
    !selectedCases.some(s => s.id === c.id) &&
    (c.testKey.toLowerCase().includes(search.toLowerCase()) ||
      c.title.toLowerCase().includes(search.toLowerCase()))
  ) ?? [];

  return (
    <div className="space-y-6">
      {isEdit && suite.isError && (
        <ErrorState error={suite.error} onRetry={() => void suite.refetch()} />
      )}
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <Link to={isEdit ? `/projects/${projectId}/test-suites/${suiteId}` : `/projects/${projectId}/test-suites`} className="text-sm text-brand-700 hover:text-brand-600">
            ← {isEdit ? 'Back to suite' : 'Back to suites'}
          </Link>
          <h1 className="mt-2 text-xl font-semibold text-slate-900">{isEdit ? 'Edit test suite' : 'Create test suite'}</h1>
          <p className="mt-1 text-sm text-slate-500">
            {isEdit ? 'Update suite metadata and manage members.' : 'Create a new suite to organize test cases for manual execution.'}
          </p>
        </div>
      </div>

      {errors.form && (
        <div className="rounded-md bg-red-50 border border-red-200 p-3 text-sm text-red-700 flex items-center gap-2">
          <AlertCircle className="h-4 w-4" aria-hidden />
          {errors.form}
        </div>
      )}

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Suite details</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <form onSubmit={handleSubmit} className="space-y-4">
            <div>
              <label htmlFor="name" className="block text-sm font-medium text-slate-700">Name *</label>
              <Input
                id="name"
                value={name}
                onChange={(e) => { setName(e.target.value); if (errors.name) setErrors(e => ({ ...e, name: '' })); }}
                placeholder="Suite name"
                maxLength={200}
                aria-invalid={!!errors.name}
                aria-describedby={errors.name ? 'name-error' : undefined}
              />
              {errors.name && <p id="name-error" className="mt-1 text-sm text-red-600">{errors.name}</p>}
            </div>

            <div>
              <label htmlFor="description" className="block text-sm font-medium text-slate-700">Description</label>
              <Textarea
                id="description"
                value={description}
                onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setDescription(e.target.value)}
                placeholder="Optional description"
                rows={3}
              />
            </div>

            <div>
              <label htmlFor="status" className="block text-sm font-medium text-slate-700">Status</label>
              <select
                id="status"
                value={status}
                onChange={(e: React.ChangeEvent<HTMLSelectElement>) => setStatus(e.target.value)}
                className="rounded-md border border-slate-300 bg-white px-3 py-2 text-sm font-normal text-slate-900 w-full max-w-xs"
              >
                {STATUS_OPTIONS.map((s) => (
                  <option key={s} value={s}>{s}</option>
                ))}
              </select>
            </div>

          </form>
        </CardContent>
      </Card>

      {isEdit && (
        <Card>
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle className="text-base">Members ({selectedCases.length})</CardTitle>
            <div className="flex gap-2">
              {canManage && (
                <Button variant="secondary" size="sm" onClick={saveOrder} disabled={selectedCases.length < 2}>
                  Save order
                </Button>
              )}
            </div>
          </CardHeader>
          <CardContent>
            {selectedCases.length === 0 ? (
              <div className="text-center py-8 text-slate-500">
                <p>No test cases added yet.</p>
                <p className="text-sm">Search and select test cases below to add them to this suite.</p>
              </div>
            ) : (
              <div className="space-y-2">
                {selectedCases.map((tc, index) => (
                  <div key={tc.id} className="flex items-center gap-3 p-3 border rounded-lg bg-slate-50">
                    <Button variant="ghost" size="sm" onClick={() => moveUp(index)} disabled={index === 0} aria-label="Move up" className="p-1">
                      <GripVertical className="h-4 w-4" aria-hidden />
                    </Button>
                    <div className="flex-1 min-w-0">
                      <div className="flex items-center gap-2">
                        <span className="font-mono text-sm text-slate-700">{tc.testKey}</span>
                        <Badge tone={statusTone('Active')}>{index + 1}</Badge>
                      </div>
                      <p className="text-sm text-slate-500 truncate">{tc.title}</p>
                    </div>
                    <Button variant="ghost" size="sm" onClick={() => moveDown(index)} disabled={index === selectedCases.length - 1} aria-label="Move down" className="p-1">
                      <GripVertical className="h-4 w-4" aria-hidden />
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => removeTestCase(tc.id)} aria-label={`Remove ${tc.testKey}`} className="p-1">
                      <X className="h-4 w-4 text-red-600" aria-hidden />
                    </Button>
                  </div>
                ))}
              </div>
            )}
          </CardContent>
        </Card>
      )}

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Add test cases</CardTitle>
        </CardHeader>
          <CardContent className="space-y-4">
            <div className="flex gap-2">
              <Input
                placeholder="Search by key or title…"
                value={search}
                onChange={(e: React.ChangeEvent<HTMLInputElement>) => setSearch(e.target.value)}
                className="max-w-md"
              />
            </div>

            {availableCases.isLoading && (
              <div className="space-y-2">
                {[0, 1, 2].map((i) => <div key={i} className="h-10 animate-pulse bg-slate-100 rounded" />)}
              </div>
            )}

            {availableCases.isError && <ErrorState error={availableCases.error} onRetry={() => void availableCases.refetch()} />}

            {filteredCases.length === 0 && availableCases.data && !availableCases.isLoading && (
              <p className="text-sm text-slate-500 text-center py-4">
                {search ? 'No matching test cases found.' : 'All available test cases are already in the suite.'}
              </p>
            )}

            {filteredCases.length > 0 && (
              <div className="max-h-60 overflow-y-auto space-y-1 border rounded-lg p-2 bg-slate-50">
                {filteredCases.map((tc: TestCaseListItem) => (
                  <button
                    key={tc.id}
                    type="button"
                    onClick={() => addTestCase(tc)}
                    className="w-full text-left px-3 py-2 text-sm hover:bg-white rounded border border-slate-200 transition-colors flex items-center justify-between"
                  >
                    <div>
                      <span className="font-mono text-slate-700">{tc.testKey}</span>
                      <span className="ml-2 text-slate-500">{tc.title}</span>
                    </div>
                    <Plus className="h-4 w-4 text-brand-600" aria-hidden />
                  </button>
                ))}
              </div>
            )}
          </CardContent>
        </Card>

      <div className="flex justify-end gap-3 pt-4 border-t">
        <Link to={isEdit ? `/projects/${projectId}/test-suites/${suiteId}` : `/projects/${projectId}/test-suites`}>
          <Button variant="secondary">Cancel</Button>
        </Link>
        <Button onClick={handleSubmit} disabled={submitting}>
          {isEdit ? (submitting ? 'Saving…' : 'Save changes') : (submitting ? 'Creating…' : 'Create suite')}
        </Button>
      </div>
    </div>
  );
}