#define _GNU_SOURCE

#include <errno.h>
#include <limits.h>
#include <sched.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/sysmacros.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#include "cmdline.h"

pid_t child_proc = -1;
struct gengetopt_args_info args_info;
#define CAP_SYS_ADMIN_IDX 21
#define CAP_SYS_ADMIN_BIT (1ULL << CAP_SYS_ADMIN_IDX)

static void intHan(int signum) {
    if (child_proc != -1) {
        kill(child_proc, SIGKILL);
    }
}

int has_cap_sys_admin() {
    FILE *fp;
    char line[256];
    unsigned long long cap_eff = 0;
    int found_cap_eff = 0;

    fp = fopen("/proc/self/status", "r");
    if (fp == NULL) {
        return 0;
    }

    while (fgets(line, sizeof(line), fp) != NULL) {
        if (strncmp(line, "CapEff:", 7) == 0) {
            char *value_str = line + 7;
            while (*value_str == '\t' || *value_str == ' ') {
                value_str++;
            }
            cap_eff = strtoull(value_str, NULL, 16);
            found_cap_eff = 1;
            break;
        }
    }

    fclose(fp);

    if (!found_cap_eff) {
        return 0;
    }

    if (cap_eff & CAP_SYS_ADMIN_BIT) {
        return 1;
    } else {
        return 0;
    }
}

static int build_path(char *buffer, size_t buffer_len, const char *rootfs, const char *suffix) {
    if (snprintf(buffer, buffer_len, "%s/%s", rootfs, suffix) >= (int)buffer_len) {
        return 0;
    }
    return 1;
}

int main(int argc, char *argv[], char *envp[]) {
    cmdline_parser(argc, argv, &args_info);
    if (signal(SIGINT, intHan) == SIG_ERR) {
        perror("signal");
        return 1;
    }

    const char *rootfs_env = getenv("WRAPPER_ROOTFS");
    const char *rootfs_input = (rootfs_env && rootfs_env[0] != '\0') ? rootfs_env : "./rootfs";
    char rootfs_path[PATH_MAX];
    if (realpath(rootfs_input, rootfs_path) == NULL) {
        perror("realpath");
        return 1;
    }

    char main_path[PATH_MAX];
    if (!build_path(main_path, sizeof(main_path), rootfs_path, "system/bin/main")) {
        fprintf(stderr, "[!] rootfs path too long\n");
        return 1;
    }
    char linker_path[PATH_MAX];
    if (!build_path(linker_path, sizeof(linker_path), rootfs_path, "system/bin/linker64")) {
        fprintf(stderr, "[!] rootfs path too long\n");
        return 1;
    }

    chmod(main_path, 0755);
    chmod(linker_path, 0755);

    if (has_cap_sys_admin()) {
        if (unshare(CLONE_NEWPID)) {
            perror("unshare");
            return 1;
        }
    }

    child_proc = fork();
    if (child_proc == -1) {
        perror("fork");
        return 1;
    }

    if (child_proc > 0) {
        wait(NULL);
        return 0;
    }

    // Child process logic
    mkdir(args_info.base_dir_arg, 0777);
    mkdir(strcat(args_info.base_dir_arg, "/mpl_db"), 0777);
    char **proot_argv = calloc((size_t)argc + 6, sizeof(char *));
    if (proot_argv == NULL) {
        perror("calloc");
        return 1;
    }
    int idx = 0;
    proot_argv[idx++] = "proot";
    proot_argv[idx++] = "-S";
    proot_argv[idx++] = rootfs_path;
    proot_argv[idx++] = "/system/bin/main";
    for (int i = 1; i < argc; i++) {
        proot_argv[idx++] = argv[i];
    }
    proot_argv[idx] = NULL;

    execvp("proot", proot_argv);
    perror("execve");
    return 1;
}
