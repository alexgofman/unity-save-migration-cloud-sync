// PlayFab CloudScript (legacy): lets a signed-in player delete their own account.
//
// A sample. It has not been deployed or run against a live title as part of this
// repository. Deleting a master player account cannot be undone: read the handler, try it
// on a development title, and adapt it before relying on it.
//
// How it works
//   The Admin API that deletes an account is not exposed on CloudScript's `server` object,
//   so the handler calls it over HTTP with a title secret key. The key is read from Title
//   Internal Data when the handler runs. Internal data can only be read by server-side
//   code, so the key is not in this file, not in the revision history and not in the game
//   client.
//
// Setting up
//   1. In Game Manager, add an entry to the title's internal data whose key is the value of
//      SECRET_KEY_ENTRY below and whose value is a secret key of the title. Use a key
//      created for this purpose so it can be rotated on its own.
//   2. Add this file to the title's CloudScript revision. A revision is the complete set of
//      files, not a patch: uploading this file alone removes every other handler the title
//      has. Put all of them in the same revision.
//
// What keeps it safe
//   The account that is deleted is always `currentPlayerId`, which the service derives from
//   the caller's session. Nothing in `args` is used, so a player can only delete themselves.
//
// The client side is PlayFabAccountDeletion.RequestServerDeleteAsync in this package. It
// reads the boolean `accountDeleted` from the result.

var SECRET_KEY_ENTRY = "AccountDeletionSecretKey";
var ADMIN_DELETE_PATH = "/Admin/DeleteMasterPlayerAccount";

handlers.RequestAccountDeletion = function (args, context) {
    var playerId = currentPlayerId;
    if (!playerId) {
        return { accountDeleted: false, reason: "not_signed_in" };
    }

    var secretKey = readSecretKey();
    if (!secretKey) {
        log.error("RequestAccountDeletion: title internal data has no '" + SECRET_KEY_ENTRY + "' entry.");
        return { accountDeleted: false, reason: "not_configured" };
    }

    var url = "https://" + script.titleId + ".playfabapi.com" + ADMIN_DELETE_PATH;
    var body = JSON.stringify({ PlayFabId: playerId });

    try {
        var answer = JSON.parse(http.request(url, "post", body, "application/json", { "X-SecretKey": secretKey }));
        var deleted = !!answer && answer.code === 200;
        log.info("RequestAccountDeletion: " + playerId + " -> " + (answer ? answer.code : "no answer"));
        return { accountDeleted: deleted, reason: deleted ? null : "refused_by_service" };
    } catch (failure) {
        log.error("RequestAccountDeletion: " + playerId + " failed: " + failure);
        return { accountDeleted: false, reason: "request_failed" };
    }
};

function readSecretKey() {
    var internal = server.GetTitleInternalData({ Keys: [SECRET_KEY_ENTRY] });
    return internal && internal.Data ? internal.Data[SECRET_KEY_ENTRY] : null;
}
