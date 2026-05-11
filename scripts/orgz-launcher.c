// Dev-mode Mach-O launcher for OrgZ.app on macOS.
//
// The .NET-generated apphost (`bin/.../OrgZ`) is ad-hoc signed; macOS's
// SCSITaskUserClient refuses to grant SCSI access to ad-hoc binaries, which
// breaks CD reading. Apple-signed `dotnet` is allowed. This tiny shim sits as
// the .app bundle's CFBundleExecutable: macOS launches OrgZ.app and reads the
// proper bundle identity (icon, name, click-to-foreground) from Info.plist,
// then we exec into `dotnet exec OrgZ.dll` so the *running* process is the
// signed runtime host. Best of both worlds during dev — drop this whole shim
// once Velopack pack signs the apphost with Developer ID.
//
// Build:
//   clang -O2 -arch arm64 -o orgz-launcher orgz-launcher.c
//   (or both arches: -arch arm64 -arch x86_64)

#include <errno.h>
#include <libgen.h>
#include <mach-o/dyld.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/syslimits.h>
#include <unistd.h>

static int file_exists(const char *path) {
    struct stat st;
    return stat(path, &st) == 0 && (S_ISREG(st.st_mode) || S_ISLNK(st.st_mode));
}

int main(int argc, char *argv[]) {
    char self[PATH_MAX];
    uint32_t self_size = sizeof(self);
    if (_NSGetExecutablePath(self, &self_size) != 0) {
        fprintf(stderr, "orgz-launcher: _NSGetExecutablePath buffer too small\n");
        return 1;
    }
    // self = .../OrgZ.app/Contents/MacOS/OrgZ
    char *dir = dirname(self);

    char dll_path[PATH_MAX];
    snprintf(dll_path, sizeof(dll_path), "%s/OrgZ.dll", dir);
    if (!file_exists(dll_path)) {
        fprintf(stderr, "orgz-launcher: OrgZ.dll not found at %s\n", dll_path);
        return 1;
    }

    // Prefer DOTNET_ROOT if the parent shell set it; otherwise the common
    // Homebrew / Microsoft installer location. Last-ditch: PATH lookup via
    // execvp.
    const char *dotnet_root = getenv("DOTNET_ROOT");
    char dotnet[PATH_MAX];
    if (dotnet_root && *dotnet_root) {
        snprintf(dotnet, sizeof(dotnet), "%s/dotnet", dotnet_root);
    } else {
        strncpy(dotnet, "/usr/local/share/dotnet/dotnet", sizeof(dotnet));
        dotnet[sizeof(dotnet) - 1] = '\0';
    }

    int new_argc = argc + 2; // "exec" + dll_path replace argv[0]
    char **new_argv = (char **)calloc((size_t)new_argc + 1, sizeof(char *));
    if (!new_argv) {
        fprintf(stderr, "orgz-launcher: out of memory\n");
        return 1;
    }
    new_argv[0] = dotnet;
    new_argv[1] = (char *)"exec";
    new_argv[2] = dll_path;
    for (int i = 1; i < argc; i++) {
        new_argv[2 + i] = argv[i];
    }
    new_argv[new_argc] = NULL;

    if (file_exists(dotnet)) {
        execv(dotnet, new_argv);
    } else {
        execvp("dotnet", new_argv);
    }
    fprintf(stderr, "orgz-launcher: exec failed: %s\n", strerror(errno));
    return 1;
}
