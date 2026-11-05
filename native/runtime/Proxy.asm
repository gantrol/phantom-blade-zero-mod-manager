; Forward the complete System32 version.dll API while preserving integer arguments.
EXTERN ResolveVersionExport:PROC
.code
FORWARD MACRO index
Proxy&index PROC FRAME
    sub rsp, 48h
    .allocstack 48h
    .endprolog
    mov [rsp+20h], rcx
    mov [rsp+28h], rdx
    mov [rsp+30h], r8
    mov [rsp+38h], r9
    mov ecx, index
    call ResolveVersionExport
    mov rcx, [rsp+20h]
    mov rdx, [rsp+28h]
    mov r8, [rsp+30h]
    mov r9, [rsp+38h]
    add rsp, 48h
    jmp rax
Proxy&index ENDP
ENDM
FORWARD 0
FORWARD 1
FORWARD 2
FORWARD 3
FORWARD 4
FORWARD 5
FORWARD 6
FORWARD 7
FORWARD 8
FORWARD 9
FORWARD 10
FORWARD 11
FORWARD 12
FORWARD 13
FORWARD 14
FORWARD 15
FORWARD 16
END
