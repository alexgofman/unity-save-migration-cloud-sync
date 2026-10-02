# PlayFab CloudScript sample: account deletion

`RequestAccountDeletion.js` is a legacy CloudScript handler that deletes the account of the player
who calls it. It goes with `PlayFabAccountDeletion.RequestServerDeleteAsync` in the package.

It is a sample. It has not been deployed or run against a live title as part of this repository.
Deleting a master player account cannot be undone, so read it, try it on a development title and
adapt it before you rely on it.

## Why a server-side handler

A "delete my account and data" request has three parts:

| What | Who can do it |
|---|---|
| Delete the cloud save | The client (`ICloudSaveBackend.DeleteAsync`) |
| Remove the device link and forget the session | The client (`PlayFabAccountDeletion.ForgetDeviceAsync`) |
| Delete the account itself | Only server-side code that holds a title secret key |

A title secret key must never be in a client build, so the third part needs a handler like this
one.

## Setting up

1. In Game Manager, add an entry to the title's **internal** data. The key is the value of
   `SECRET_KEY_ENTRY` at the top of the script; the value is a secret key of the title. The script
   contains no title id and no key: the title id comes from `script.titleId` and the secret is read
   from internal data when the handler runs.
2. Add the file to the title's CloudScript revision. A revision is the complete set of files, not a
   patch. Uploading this file alone removes every other handler the title has, without a warning.

## Calling it

```csharp
// While the session is still valid: ask the server first.
CloudResult<bool> server = await PlayFabAccountDeletion.RequestServerDeleteAsync();

// Then remove what the client can remove, whatever the server said.
coordinator.Suspend();                                    // nothing may be uploaded from here on
await backend.DeleteAsync(CancellationToken.None);        // the cloud save
await PlayFabAccountDeletion.ForgetDeviceAsync(customId); // the device link and the cached session
session.Erase();                                          // the local save and the state in memory
```

`RequestServerDeleteAsync` returns `Ok(true)` only when the handler ran and returned a boolean
`accountDeleted` that is true. It reads that field by key and type. A handler that is not deployed,
throws, or returns something else produces a failure, and the client-side steps still run.
