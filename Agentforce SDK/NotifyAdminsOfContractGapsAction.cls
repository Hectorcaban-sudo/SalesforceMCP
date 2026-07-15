/**
 * Invocable Action: Send Admin Notification Email
 *
 * Takes the summary produced by FindIncompleteContractsAction and
 * emails it to the org's System Administrator users (or an explicit
 * list of addresses passed in by the agent).
 *
 * Deploy this class to your org, then reference it from the agent
 * definition (see create_contract_agent.py) as:
 *   action_name = "NotifyAdminsOfContractGapsAction"
 */
public with sharing class NotifyAdminsOfContractGapsAction {

    public class Request {
        @InvocableVariable(label='Summary Text' required=true description='Plain-text summary of incomplete contracts to send')
        public String summaryText;

        @InvocableVariable(label='Total Incomplete' description='Count of incomplete contracts, used in the subject line')
        public Integer totalIncomplete;

        @InvocableVariable(label='Explicit Recipient Emails' description='Optional comma-separated list of emails. If blank, all active System Administrator profile users are notified.')
        public String recipientEmails;
    }

    public class Response {
        @InvocableVariable public Boolean success;
        @InvocableVariable public String resultMessage;
    }

    @InvocableMethod(
        label='Send Admin Notification Email'
        description='Emails a summary of contracts with missing data to Salesforce admins.'
        category='Contract Data Quality'
    )
    public static List<Response> notifyAdmins(List<Request> requests) {
        Request req = (requests != null && !requests.isEmpty()) ? requests[0] : new Request();
        Response resp = new Response();

        if (String.isBlank(req.summaryText)) {
            resp.success = false;
            resp.resultMessage = 'No summary text provided; nothing to send.';
            return new List<Response>{ resp };
        }

        List<String> toAddresses = new List<String>();
        if (String.isNotBlank(req.recipientEmails)) {
            for (String addr : req.recipientEmails.split(',')) {
                if (String.isNotBlank(addr)) {
                    toAddresses.add(addr.trim());
                }
            }
        } else {
            toAddresses = getSystemAdminEmails();
        }

        if (toAddresses.isEmpty()) {
            resp.success = false;
            resp.resultMessage = 'No admin recipients found or provided.';
            return new List<Response>{ resp };
        }

        Integer count = req.totalIncomplete != null ? req.totalIncomplete : 0;
        String subject = 'Action Needed: ' + count + ' Contract(s) with Missing Data';

        String body = 'Hello,\n\n' +
            'An automated review found the following Contract records with missing required data.\n' +
            'Please review and update them at your earliest convenience.\n\n' +
            req.summaryText +
            '\n\nThis is an automated notification from the Contract Data Quality Agent.';

        Messaging.SingleEmailMessage mail = new Messaging.SingleEmailMessage();
        mail.setToAddresses(toAddresses);
        mail.setSubject(subject);
        mail.setPlainTextBody(body);
        mail.setSaveAsActivity(false);

        try {
            Messaging.SendEmailResult[] results = Messaging.sendEmail(new List<Messaging.SingleEmailMessage>{ mail });
            Boolean allOk = true;
            for (Messaging.SendEmailResult r : results) {
                if (!r.isSuccess()) {
                    allOk = false;
                }
            }
            resp.success = allOk;
            resp.resultMessage = allOk
                ? 'Email sent to ' + toAddresses.size() + ' admin(s): ' + String.join(toAddresses, ', ')
                : 'Email send reported one or more failures.';
        } catch (Exception e) {
            resp.success = false;
            resp.resultMessage = 'Failed to send email: ' + e.getMessage();
        }

        return new List<Response>{ resp };
    }

    private static List<String> getSystemAdminEmails() {
        List<String> emails = new List<String>();
        for (User u : [
            SELECT Email
            FROM User
            WHERE IsActive = true
              AND Profile.Name = 'System Administrator'
              AND Email != null
        ]) {
            emails.add(u.Email);
        }
        return emails;
    }
}
