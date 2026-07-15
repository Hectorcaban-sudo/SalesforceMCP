/**
 * Invocable Action: Find Contracts With Missing Data
 *
 * Scans Contract records for missing/blank fields that are commonly
 * required for a contract to be considered "complete" (customizable
 * via the fieldsToCheck set below), and returns a structured summary
 * that the Agentforce agent can reason over and hand off to the
 * "Send Admin Notification Email" action.
 *
 * Deploy this class to your org, then reference it from the agent
 * definition (see create_contract_agent.py) as:
 *   action_name = "FindIncompleteContractsAction"
 */
public with sharing class FindIncompleteContractsAction {

    // Adjust this list to match the fields your org considers "required"
    // for a Contract to be complete. Must be valid API names on Contract.
    private static final List<String> FIELDS_TO_CHECK = new List<String>{
        'StartDate',
        'EndDate',
        'ContractTerm',
        'OwnerExpirationNotice',
        'Description',
        'AccountId',
        'BillingStreet',
        'BillingCity'
    };

    public class Request {
        @InvocableVariable(label='Max Records' description='Maximum number of contracts to scan (default 200)')
        public Integer maxRecords;

        @InvocableVariable(label='Only Activated Contracts' description='If true, only scans contracts with Status = Activated')
        public Boolean onlyActivated;
    }

    public class ContractGap {
        @InvocableVariable public Id contractId;
        @InvocableVariable public String contractNumber;
        @InvocableVariable public String accountName;
        @InvocableVariable public String status;
        @InvocableVariable public String missingFields; // comma-separated, human readable
        @InvocableVariable public String recordUrl;
    }

    public class Response {
        @InvocableVariable public Integer totalScanned;
        @InvocableVariable public Integer totalIncomplete;
        @InvocableVariable public List<ContractGap> incompleteContracts;
        @InvocableVariable public String summaryText; // ready-to-use plain text summary
    }

    @InvocableMethod(
        label='Find Contracts With Missing Data'
        description='Scans Contract records and returns those missing key required fields, for review by an admin.'
        category='Contract Data Quality'
    )
    public static List<Response> findIncompleteContracts(List<Request> requests) {
        Request req = (requests != null && !requests.isEmpty()) ? requests[0] : new Request();
        Integer lim = (req.maxRecords != null) ? req.maxRecords : 200;
        Boolean onlyActivated = req.onlyActivated == true;

        String baseUrl = URL.getOrgDomainUrl().toExternalForm();

        String query = 'SELECT Id, ContractNumber, Status, StartDate, EndDate, ContractTerm, ' +
                        'OwnerExpirationNotice, Description, AccountId, Account.Name, ' +
                        'BillingStreet, BillingCity ' +
                        'FROM Contract ';
        if (onlyActivated) {
            query += 'WHERE Status = \'Activated\' ';
        }
        query += 'ORDER BY LastModifiedDate DESC LIMIT :lim';

        List<Contract> contracts = Database.query(query);

        List<ContractGap> gaps = new List<ContractGap>();

        for (Contract c : contracts) {
            List<String> missing = new List<String>();

            if (c.StartDate == null) missing.add('Start Date');
            if (c.EndDate == null) missing.add('End Date');
            if (c.ContractTerm == null) missing.add('Contract Term');
            if (c.OwnerExpirationNotice == null) missing.add('Owner Expiration Notice');
            if (String.isBlank(c.Description)) missing.add('Description');
            if (c.AccountId == null) missing.add('Account');
            if (String.isBlank(c.BillingStreet)) missing.add('Billing Street');
            if (String.isBlank(c.BillingCity)) missing.add('Billing City');

            if (!missing.isEmpty()) {
                ContractGap g = new ContractGap();
                g.contractId = c.Id;
                g.contractNumber = c.ContractNumber;
                g.accountName = c.Account != null ? c.Account.Name : '(No Account)';
                g.status = c.Status;
                g.missingFields = String.join(missing, ', ');
                g.recordUrl = baseUrl + '/' + c.Id;
                gaps.add(g);
            }
        }

        Response resp = new Response();
        resp.totalScanned = contracts.size();
        resp.totalIncomplete = gaps.size();
        resp.incompleteContracts = gaps;
        resp.summaryText = buildSummaryText(gaps, contracts.size());

        return new List<Response>{ resp };
    }

    private static String buildSummaryText(List<ContractGap> gaps, Integer totalScanned) {
        if (gaps.isEmpty()) {
            return 'Scanned ' + totalScanned + ' contract(s). No missing-data issues found.';
        }
        List<String> lines = new List<String>();
        lines.add('Scanned ' + totalScanned + ' contract(s). Found ' + gaps.size() + ' with missing data:\n');
        for (ContractGap g : gaps) {
            lines.add('- ' + g.contractNumber + ' (' + g.accountName + ', status: ' + g.status +
                       ') — missing: ' + g.missingFields + ' — ' + g.recordUrl);
        }
        return String.join(lines, '\n');
    }
}
