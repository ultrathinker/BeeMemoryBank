using Android.App;

// All permissions must be declared via [assembly: UsesPermission] attributes.
// .NET 10 MAUI does NOT merge <uses-permission> from Platforms/Android/AndroidManifest.xml.
[assembly: UsesPermission(Android.Manifest.Permission.Internet)]
[assembly: UsesPermission(Android.Manifest.Permission.AccessNetworkState)]
[assembly: UsesPermission(Android.Manifest.Permission.ForegroundService)]
[assembly: UsesPermission("android.permission.FOREGROUND_SERVICE_DATA_SYNC")]
[assembly: UsesPermission(Android.Manifest.Permission.ReceiveBootCompleted)]
[assembly: UsesPermission("android.permission.POST_NOTIFICATIONS")]
[assembly: UsesPermission("android.permission.REQUEST_IGNORE_BATTERY_OPTIMIZATIONS")]
// .NET MAUI's Battery API refuses to read the charge level and state unless this is declared (Permissions.EnsureDeclared); the encryption's preflight
// (battery at least 30 % or charging, spec 10.8) reads both. Declaring it grants nothing: the values come from the sticky battery broadcast.
[assembly: UsesPermission("android.permission.BATTERY_STATS")]
