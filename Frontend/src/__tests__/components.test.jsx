import { afterEach, describe, expect, it, vi } from 'vitest';
import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { DataTable } from '../components/DataTable.jsx';
import { Pagination } from '../components/Pagination.jsx';
import { PriorityBadge, RiskBadge, SeverityBadge, StatusBadge, TierBadge, WorkOrderStatusBadge, badgeTone } from '../components/StatusBadge.jsx';
import { WO_STATUS, workOrderStatusOptions } from '../utils/enums.js';
import { workOrderColumns } from '../pages/shared/columns.jsx';
import { NOT_VALIDATED_TEXT, ProbabilityValue } from '../components/Probability.jsx';
import { Modal } from '../components/Modal.jsx';
import { PredictionDetail } from '../pages/predictions/PredictionsPage.jsx';
import { renderApp } from '../test/utils.jsx';

afterEach(() => vi.unstubAllGlobals());

describe('DataTable', () => {
  const columns = [
    { key: 'reg', header: 'Registration', sortable: true },
    { key: 'hours', header: 'Hours', sortable: true, numeric: true },
    { key: 'note', header: 'Note' },
  ];
  const rows = [
    { id: 1, reg: 'DEMO-A03', hours: 120, note: 'b' },
    { id: 2, reg: 'DEMO-A01', hours: 900, note: 'a' },
    { id: 3, reg: 'DEMO-A02', hours: 15, note: 'c' },
  ];
  const firstColumn = () => screen.getAllByRole('row').slice(1).map((r) => within(r).getAllByRole('cell')[0].textContent);

  it('renders headers and rows', () => {
    render(<DataTable columns={columns} rows={rows} caption="Aircraft" />);
    expect(screen.getByRole('table', { name: 'Aircraft' })).toBeInTheDocument();
    expect(screen.getAllByRole('columnheader')).toHaveLength(3);
    expect(firstColumn()).toEqual(['DEMO-A03', 'DEMO-A01', 'DEMO-A02']);
  });

  it('sorts ascending, descending, then back to original with aria-sort', async () => {
    const user = userEvent.setup();
    render(<DataTable columns={columns} rows={rows} />);
    const btn = screen.getByRole('button', { name: /Registration/ });
    const th = btn.closest('th');
    expect(th).toHaveAttribute('aria-sort', 'none');
    await user.click(btn);
    expect(th).toHaveAttribute('aria-sort', 'ascending');
    expect(firstColumn()).toEqual(['DEMO-A01', 'DEMO-A02', 'DEMO-A03']);
    await user.click(btn);
    expect(th).toHaveAttribute('aria-sort', 'descending');
    expect(firstColumn()).toEqual(['DEMO-A03', 'DEMO-A02', 'DEMO-A01']);
    await user.click(btn);
    expect(firstColumn()).toEqual(['DEMO-A03', 'DEMO-A01', 'DEMO-A02']);
  });

  it('sorts numerically and is keyboard operable', async () => {
    const user = userEvent.setup();
    render(<DataTable columns={columns} rows={rows} />);
    screen.getByRole('button', { name: /Hours/ }).focus();
    await user.keyboard('{Enter}');
    expect(firstColumn()).toEqual(['DEMO-A02', 'DEMO-A03', 'DEMO-A01']);
    // non-sortable columns render no button
    expect(screen.queryByRole('button', { name: /Note/ })).not.toBeInTheDocument();
  });

  it('shows an empty state when there are no rows', () => {
    render(<DataTable columns={columns} rows={[]} emptyTitle="No aircraft found" />);
    expect(screen.getByText('No aircraft found')).toBeInTheDocument();
    expect(screen.queryByRole('table')).not.toBeInTheDocument();
  });
});

describe('Pagination', () => {
  it('shows range and disables Previous on the first page', async () => {
    const onPageChange = vi.fn();
    const user = userEvent.setup();
    render(<Pagination page={1} pageSize={25} totalCount={60} totalPages={3} onPageChange={onPageChange} />);
    expect(screen.getByText('Showing 1–25 of 60')).toBeInTheDocument();
    expect(screen.getByText('Page 1 of 3')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Previous' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Next' }));
    expect(onPageChange).toHaveBeenCalledWith(2);
  });

  it('disables Next on the last page and changes page size', async () => {
    const onPageChange = vi.fn();
    const onPageSizeChange = vi.fn();
    const user = userEvent.setup();
    render(<Pagination page={3} pageSize={25} totalCount={60} totalPages={3} onPageChange={onPageChange} onPageSizeChange={onPageSizeChange} />);
    expect(screen.getByText('Showing 51–60 of 60')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Previous' }));
    expect(onPageChange).toHaveBeenCalledWith(2);
    await user.selectOptions(screen.getByRole('combobox'), '50');
    expect(onPageSizeChange).toHaveBeenCalledWith(50);
  });

  it('handles zero results', () => {
    render(<Pagination page={1} pageSize={25} totalCount={0} totalPages={0} onPageChange={() => {}} />);
    expect(screen.getByText('No results')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled();
  });
});

describe('StatusBadge', () => {
  it('renders human-readable labels with a tone', () => {
    render(
      <>
        <StatusBadge value="UnderMaintenance" />
        <StatusBadge value="Grounded" />
        <StatusBadge value="NoKnownRestrictions" />
        <StatusBadge value="InInspection" />
      </>,
    );
    expect(screen.getByText('Under maintenance')).toHaveAttribute('data-tone', 'warning');
    expect(screen.getByText('Grounded')).toHaveAttribute('data-tone', 'danger');
    expect(screen.getByText('No known restrictions')).toHaveAttribute('data-tone', 'success');
    expect(screen.getByText('In inspection')).toBeInTheDocument();
  });

  it('applies kind-specific tones and accessible prefixes', () => {
    render(
      <>
        <RiskBadge value="Low" />
        <SeverityBadge value="High" />
        <PriorityBadge value="Critical" />
        <RiskBadge value={null} />
      </>,
    );
    expect(screen.getByText('Low').closest('.badge')).toHaveAttribute('data-tone', 'success');
    expect(screen.getByText('Severity:', { exact: false })).toHaveClass('visually-hidden');
    expect(screen.getByText('Critical').closest('.badge')).toHaveAttribute('data-tone', 'danger');
    expect(screen.getByText('Undetermined')).toBeInTheDocument();
    expect(badgeTone('Low', 'severity')).toBe('neutral');
    expect(badgeTone('Low', 'risk')).toBe('success');
  });

  it('renders a dash for empty values', () => {
    render(<StatusBadge value={null} />);
    expect(screen.getByText('—')).toBeInTheDocument();
  });
});

describe('Work-order status labels', () => {
  const EXPECTED = { Open: 'NEW', Assigned: 'ASSIGNED', InInspection: 'IN_PROGRESS', Resolved: 'REPORTED', Closed: 'COMPLETED', Cancelled: 'CANCELLED' };

  it.each(Object.entries(EXPECTED))('renders work-order status %s as %s, keeping text and tone', (value, label) => {
    render(<WorkOrderStatusBadge value={value} />);
    const badge = screen.getByText(label).closest('.badge');
    expect(badge).toHaveAttribute('data-tone', badgeTone(value));
    expect(within(badge).getByText('Work order status:', { exact: false })).toHaveClass('visually-hidden');
  });

  it('renders a work order with status "Resolved" as REPORTED and "Closed" as COMPLETED in the work-order table', () => {
    renderApp(
      <DataTable
        columns={workOrderColumns({ linkAircraft: false })}
        rows={[
          { id: 'w1', workOrderNumber: 'WO-1', title: 'Reported one', registrationNumber: 'N1', priority: 'High', status: 'Resolved' },
          { id: 'w2', workOrderNumber: 'WO-2', title: 'Completed one', registrationNumber: 'N2', priority: 'Low', status: 'Closed' },
        ]}
        caption="Work orders"
      />,
    );
    const rows = screen.getAllByRole('row');
    expect(within(rows.find((r) => r.textContent.includes('WO-1'))).getByText('REPORTED')).toBeInTheDocument();
    expect(within(rows.find((r) => r.textContent.includes('WO-2'))).getByText('COMPLETED')).toBeInTheDocument();
    expect(screen.queryByText('Resolved')).not.toBeInTheDocument();
    expect(screen.queryByText('Closed')).not.toBeInTheDocument();
  });

  it('leaves pipeline-run, import, model and user statuses on their generic labels', () => {
    render(
      <>
        <StatusBadge value="Completed" />
        <StatusBadge value="Failed" />
        <StatusBadge value="Succeeded" />
        <StatusBadge value="Cancelled" />
        <StatusBadge value="Active" />
      </>,
    );
    expect(screen.getByText('Completed')).toHaveAttribute('data-tone', 'success');
    expect(screen.getByText('Failed')).toHaveAttribute('data-tone', 'danger');
    expect(screen.getByText('Succeeded')).toBeInTheDocument();
    expect(screen.getByText('Cancelled')).toBeInTheDocument();
    expect(screen.getByText('Active')).toBeInTheDocument();
    expect(screen.queryByText('COMPLETED')).not.toBeInTheDocument();
    expect(screen.queryByText('CANCELLED')).not.toBeInTheDocument();
  });

  it('keeps backend enum values in the work-order status select options', () => {
    expect(workOrderStatusOptions()).toEqual(WO_STATUS.map((value) => ({ value, label: EXPECTED[value] })));
  });
});

describe('Prediction probability display', () => {
  const base = {
    id: 'p1',
    aircraftId: 'a1',
    registrationNumber: 'DEMO-A01',
    predictionTimestampUtc: '2026-08-14T06:30:00Z',
    horizonDays: 30,
    riskCategory: 'Elevated',
    basis: 'DescriptiveIndicator',
    featureSnapshot: { anomalies_30d: 3 },
    modelName: null,
    modelVersion: null,
    approvalStatus: 'PendingReview',
    actualOutcome: 'Unknown',
    disclaimer: 'Advisory only; not an airworthiness determination.',
  };

  it('hides the probability when probabilityValidated is false, even if a value is present', () => {
    render(<ProbabilityValue prediction={{ ...base, probability: 0.42, probabilityValidated: false }} />);
    expect(screen.getByText(NOT_VALIDATED_TEXT)).toBeInTheDocument();
    expect(screen.queryByText(/42/)).not.toBeInTheDocument();
  });

  it('shows "Not validated — descriptive indicator" when probability is null', () => {
    render(<ProbabilityValue prediction={{ ...base, probability: null, probabilityValidated: false }} />);
    expect(screen.getByText('Not validated — descriptive indicator')).toBeInTheDocument();
  });

  it('shows the probability only when validated', () => {
    render(<ProbabilityValue prediction={{ ...base, basis: 'Model', probability: 0.1234, probabilityValidated: true }} />);
    expect(screen.getByText('12.3%')).toBeInTheDocument();
  });

  it('prediction detail shows basis, rolling period, disclaimer and the not-validated label', () => {
    renderApp(<PredictionDetail prediction={{ ...base, probability: 0.9, probabilityValidated: false }} />);
    expect(screen.getByTestId('prediction-probability')).toHaveTextContent(NOT_VALIDATED_TEXT);
    expect(screen.getByText(/Rolling 30-day window/)).toBeInTheDocument();
    expect(screen.getByText('Descriptive indicator')).toBeInTheDocument();
    expect(screen.getByText(/not an airworthiness determination/)).toBeInTheDocument();
    expect(screen.queryByText(/90/)).not.toBeInTheDocument();
  });
});

describe('Modal', () => {
  it('traps focus, closes on Escape and restores focus', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    const opener = document.createElement('button');
    opener.textContent = 'opener';
    document.body.appendChild(opener);
    opener.focus();
    const { rerender } = render(
      <Modal open title="Confirm action" onClose={onClose} footer={<button type="button">OK</button>}>
        <input aria-label="Reason" />
      </Modal>,
    );
    const dialog = screen.getByRole('dialog', { name: 'Confirm action' });
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(screen.getByLabelText('Reason')).toHaveFocus();
    await user.tab();
    expect(screen.getByRole('button', { name: 'OK' })).toHaveFocus();
    await user.tab();
    expect(screen.getByRole('button', { name: 'Close dialog' })).toHaveFocus();
    await user.tab({ shift: true });
    expect(screen.getByRole('button', { name: 'OK' })).toHaveFocus();
    await user.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
    rerender(
      <Modal open={false} title="Confirm action" onClose={onClose}>
        x
      </Modal>,
    );
    expect(opener).toHaveFocus();
    opener.remove();
  });
});

describe('BR-03 tier badge', () => {
  // Regression (2026-10-27): tiers were rendered with the descriptive-indicator map (Low/Elevated/High), which has no
  // Medium or Critical, so a Critical 30-day risk showed in neutral grey.
  it.each([
    ['Low', 'success'],
    ['Medium', 'warning'],
    ['Critical', 'danger'],
  ])('%s renders with the %s tone and its name', (tier, tone) => {
    render(<TierBadge value={tier} />);
    const badge = screen.getByText(tier);
    expect(badge.closest('.badge')).toHaveAttribute('data-tone', tone);
  });

  it('keeps the descriptive indicator mapping unchanged', () => {
    expect(badgeTone('High', 'risk')).toBe('danger');
    expect(badgeTone('Elevated', 'risk')).toBe('warning');
  });
});
