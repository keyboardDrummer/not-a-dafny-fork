// RUN: %dafny /compile:0  "%s" > "%t"
// RUN: %diff "%s.expect" "%t"

ghost function foo(s:seq<int>) : (int, int)
{
  (0, 0)
}

method pop_run_impl() {
  var a := new int[10];
  var i := 0;
  ghost var (x,y) := foo(a[..i+1]);
}