// RUN: %exits-with 0 %dafny /compile:3 "%s" > "%t"
// RUN: %diff "%s.expect" "%t"

method Main() {
  print "hello";
}