@isTest
private class EinsteinChatPublisherTest {

    @isTest
    static void publishRequest_happyPath_returnsOperationId() {
        Test.startTest();
        String opId = EinsteinChatPublisher.publishRequest(
            'conv-123',
            'chat',
            'Hello Einstein',
            '[]'
        );
        Test.stopTest();

        System.assertNotEquals(null, opId, 'Expected a non-null operation id');
    }

    @isTest
    static void publishRequest_agentMode_acceptsHistory() {
        String history = '[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"}]';

        Test.startTest();
        String opId = EinsteinChatPublisher.publishRequest(
            'conv-abc',
            'agents',
            'List expiring contracts',
            history
        );
        Test.stopTest();

        System.assertNotEquals(null, opId);
    }

    @isTest
    static void publishRequest_blankConversationId_throws() {
        Boolean threw = false;
        try {
            EinsteinChatPublisher.publishRequest('', 'chat', 'hi', '[]');
        } catch (AuraHandledException e) {
            threw = true;
        }
        System.assert(threw, 'Expected AuraHandledException for blank conversationId');
    }

    @isTest
    static void publishRequest_blankMessage_throws() {
        Boolean threw = false;
        try {
            EinsteinChatPublisher.publishRequest('conv-1', 'chat', '   ', '[]');
        } catch (AuraHandledException e) {
            threw = true;
        }
        System.assert(threw, 'Expected AuraHandledException for blank message');
    }

    @isTest
    static void publishRequest_invalidMode_throws() {
        Boolean threw = false;
        try {
            EinsteinChatPublisher.publishRequest('conv-1', 'bogus', 'hi', '[]');
        } catch (AuraHandledException e) {
            threw = true;
        }
        System.assert(threw, 'Expected AuraHandledException for invalid mode');
    }

    @isTest
    static void publishRequest_blankHistory_defaultsToEmptyArray() {
        // Just ensures the null-history branch executes without error
        Test.startTest();
        String opId = EinsteinChatPublisher.publishRequest('conv-1', 'chat', 'hi', null);
        Test.stopTest();
        System.assertNotEquals(null, opId);
    }
}
